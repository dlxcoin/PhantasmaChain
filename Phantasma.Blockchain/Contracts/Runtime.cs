using System;
using System.Collections.Generic;
using Phantasma.VM;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Core.Types;
using Phantasma.Storage.Context;
using Phantasma.Storage;
using Phantasma.Blockchain.Tokens;
using Phantasma.Domain;

namespace Phantasma.Blockchain.Contracts
{
    public class RuntimeVM : VirtualMachine, IRuntime
    {
        public Timestamp Time { get; private set; }
        public Transaction Transaction { get; private set; }
        public Chain Chain { get; private set; }
        public Chain ParentChain { get; private set; }
        public OracleReader Oracle { get; private set; }
        public Nexus Nexus => Chain.Nexus;

        private List<Event> _events = new List<Event>();
        public IEnumerable<Event> Events => _events;

        public Address FeeTargetAddress { get; private set; }

        public StorageChangeSetContext ChangeSet { get; private set; }

        public BigInteger UsedGas { get; private set; }
        public BigInteger PaidGas { get; private set; }
        public BigInteger MaxGas { get; private set; }
        public BigInteger GasPrice { get; private set; }
        public Address GasTarget { get; private set; }
        public bool DelayPayment { get; private set; }
        public readonly bool readOnlyMode;

        private bool isBlockOperation;

        private bool randomized;
        private BigInteger seed;

        public BigInteger MinimumFee;

        public RuntimeVM(byte[] script, Chain chain, Timestamp time, Transaction transaction, StorageChangeSetContext changeSet, OracleReader oracle, bool readOnlyMode, bool delayPayment = false) : base(script)
        {
            Core.Throw.IfNull(chain, nameof(chain));
            Core.Throw.IfNull(changeSet, nameof(changeSet));

            // NOTE: block and transaction can be null, required for Chain.InvokeContract
            //Throw.IfNull(block, nameof(block));
            //Throw.IfNull(transaction, nameof(transaction));

            this.MinimumFee = 1;
            this.GasPrice = 0;
            this.UsedGas = 0;
            this.PaidGas = 0;
            this.GasTarget = chain.Address;
            this.MaxGas = 10000;  // a minimum amount required for allowing calls to Gas contract etc
            this.DelayPayment = delayPayment;

            this.Time = time;
            this.Chain = chain;
            this.Transaction = transaction;
            this.Oracle = oracle;
            this.ChangeSet = changeSet;
            this.readOnlyMode = readOnlyMode;

            this.isBlockOperation = false;
            this.randomized = false;

            this.FeeTargetAddress = Address.Null;

            if (this.Chain != null && !Chain.IsRoot)
            {
                var parentName = chain.Nexus.GetParentChainByName(chain.Name);
                this.ParentChain = chain.Nexus.FindChainByName(parentName);
            }
            else
            {
                this.ParentChain = null;
            }

            ExtCalls.RegisterWithRuntime(this);
        }

        public bool IsTrigger => DelayPayment;

        INexus IRuntime.Nexus => throw new NotImplementedException();

        IChain IRuntime.Chain => throw new NotImplementedException();

        ITransaction IRuntime.Transaction => throw new NotImplementedException();

        public StorageContext Storage => throw new NotImplementedException();

        public override string ToString()
        {
            return $"Runtime.Context={CurrentContext}";
        }

        internal void RegisterMethod(string name, Func<RuntimeVM, ExecutionState> handler)
        {
            handlers[name] = handler;
        }

        private Dictionary<string, Func<RuntimeVM, ExecutionState>> handlers = new Dictionary<string, Func<RuntimeVM, ExecutionState>>();

        public override ExecutionState ExecuteInterop(string method)
        {
            Expect(!isBlockOperation, "no interops available in block operations");

            if (handlers.ContainsKey(method))
            {
                return handlers[method](this);
            }

            return ExecutionState.Fault;
        }

        public override ExecutionState Execute()
        {
            var result = base.Execute();

            if (result == ExecutionState.Halt)
            {
                if (readOnlyMode)
                {
                    if (ChangeSet.Any())
                    {
#if DEBUG
                        throw new VMDebugException(this, "VM changeset modified in read-only mode");
#else
                        result = ExecutionState.Fault;
#endif
                    }
                }
                else
                if (PaidGas < UsedGas && Nexus.HasGenesis && !DelayPayment)
                {
#if DEBUG
                    throw new VMDebugException(this, "VM unpaid gas");
#else
                                        result = ExecutionState.Fault;
#endif
                }
            }

            return result;
        }

        public override ExecutionContext LoadContext(string contextName)
        {
            if (isBlockOperation && Nexus.HasGenesis && contextName != Nexus.TokenContractName)
            {
                throw new ChainException($"{contextName} context not available in block operations");
            }

            var contract = this.Nexus.AllocContractByName(contextName);
            if (contract != null)
            {
                return Chain.GetContractContext(this.ChangeSet, contract);
            }

            return null;
        }

        public VMObject CallContext(string contextName, string methodName, params object[] args)
        {
            var previousContext = CurrentContext;
            var previousCaller = this.EntryAddress;
          
            var context = LoadContext(contextName);
            Expect(context != null, "could not call context: " + contextName);

            for (int i= args.Length - 1; i>=0; i--)
            {
                var obj = VMObject.FromObject(args[i]);
                this.Stack.Push(obj);
            }

            this.Stack.Push(VMObject.FromObject(methodName));

            BigInteger savedGas = this.UsedGas;

            this.EntryAddress = SmartContract.GetAddressForName(CurrentContext.Name);
            CurrentContext = context;
            var temp = context.Execute(this.CurrentFrame, this.Stack);
            Expect(temp == ExecutionState.Halt, "expected call success");

            CurrentContext = previousContext;
            this.EntryAddress = previousCaller;

            if (contextName == Nexus.BombContractName)
            {
                this.UsedGas = savedGas;
            }

            if (this.Stack.Count > 0)
            {
                var result = this.Stack.Pop();
                return result;
            }
            else
            {
                return new VMObject();
            }
        }

        public void Notify<T>(Enum kind, Address address, T content)
        {
            var intVal = (int)(object)kind;
            Notify<T>((EventKind)(EventKind.Custom + intVal), address, content);
        }

        public void Notify<T>(EventKind kind, Address address, T content)
        {
            var bytes = content == null ? new byte[0] : Serialization.Serialize(content);
            Notify(kind, address, bytes);
        }

        public void Notify(EventKind kind, Address address, byte[] bytes)
        {
            var contract = CurrentContext.Name;

            switch (kind)
            {
                case EventKind.GasEscrow:
                    {
                        Expect(contract == Nexus.GasContractName, $"event kind only in {Nexus.GasContractName} contract");

                        var gasInfo = Serialization.Unserialize<GasEventData>(bytes);
                        Expect(gasInfo.price >= this.MinimumFee, "gas fee is too low");
                        this.MaxGas = gasInfo.amount;
                        this.GasPrice = gasInfo.price;
                        this.GasTarget = address;
                        break;
                    }

                case EventKind.GasPayment:
                    {
                        Expect(contract == Nexus.GasContractName, $"event kind only in {Nexus.GasContractName} contract");

                        var gasInfo = Serialization.Unserialize<GasEventData>(bytes);
                        this.PaidGas += gasInfo.amount;

                        if (address != this.Chain.Address)
                        {
                            this.FeeTargetAddress = address;
                        }

                        break;
                    }

                case EventKind.GasLoan:
                    Expect(contract == Nexus.GasContractName, $"event kind only in {Nexus.GasContractName} contract");
                    break;

                case EventKind.BlockCreate:
                case EventKind.BlockClose:
                    Expect(contract == Nexus.BlockContractName, $"event kind only in {Nexus.BlockContractName} contract");

                    isBlockOperation = true;
                    UsedGas = 0;
                    break;

                case EventKind.ValidatorSwitch:
                    Expect(contract == Nexus.BlockContractName, $"event kind only in {Nexus.BlockContractName} contract");
                    break;

                case EventKind.PollCreated:
                case EventKind.PollClosed:
                case EventKind.PollVote:
                    Expect(contract == Nexus.ConsensusContractName, $"event kind only in {Nexus.ConsensusContractName} contract");
                    break;

                case EventKind.ChainCreate:
                case EventKind.TokenCreate:
                case EventKind.FeedCreate:
                    Expect(contract == Nexus.NexusContractName, $"event kind only in {Nexus.NexusContractName} contract");
                    break;

                case EventKind.FileCreate:
                case EventKind.FileDelete:
                    Expect(contract == Nexus.StorageContractName, $"event kind only in {Nexus.StorageContractName } contract");
                    break;

                case EventKind.ValidatorPropose:
                case EventKind.ValidatorElect:
                case EventKind.ValidatorRemove:
                    Expect(contract == Nexus.ValidatorContractName, $"event kind only in {Nexus.ValidatorContractName} contract");
                    break;

                case EventKind.BrokerRequest:
                    Expect(contract == Nexus.InteropContractName, $"event kind only in {Nexus.InteropContractName} contract");
                    break;

                case EventKind.ValueCreate:
                case EventKind.ValueUpdate:
                    Expect(contract == Nexus.GovernanceContractName, $"event kind only in {Nexus.GovernanceContractName} contract");
                    break;
            }

            var evt = new Event(kind, address, contract, bytes);
            _events.Add(evt);
        }

        public void Expect(bool condition, string description)
        {
#if DEBUG
            if (!condition)
            {
                throw new VMDebugException(this, description);
            }
#endif

            Core.Throw.If(!condition, $"contract assertion failed: {description}");
        }

        #region GAS
        public override ExecutionState ValidateOpcode(Opcode opcode)
        {
            // required for allowing transactions to occur pre-minting of native token
            if (readOnlyMode || !Nexus.HasGenesis)
            {
                return ExecutionState.Running;
            }

            var gasCost = GetGasCostForOpcode(opcode);
            return ConsumeGas(gasCost);
        }

        public ExecutionState ConsumeGas(BigInteger gasCost)
        {
            if (gasCost == 0 || isBlockOperation)
            {
                return ExecutionState.Running;
            }

            if (gasCost < 0)
            {
                Core.Throw.If(gasCost < 0, "invalid gas amount");
            }

            // required for allowing transactions to occur pre-minting of native token
            if (readOnlyMode || !Nexus.HasGenesis)
            {
                return ExecutionState.Running;
            }

            UsedGas += gasCost;

            if (UsedGas > MaxGas && !DelayPayment)
            {
#if DEBUG
                throw new VMDebugException(this, "VM gas limit exceeded");
#else
                                return ExecutionState.Fault;
#endif
            }

            return ExecutionState.Running;
        }

        public static BigInteger GetGasCostForOpcode(Opcode opcode)
        {
            switch (opcode)
            {
                case Opcode.GET:
                case Opcode.PUT:
                case Opcode.CALL:
                case Opcode.LOAD:
                    return 2;

                case Opcode.EXTCALL:
                    return 3;

                case Opcode.CTX:
                    return 5;

                case Opcode.SWITCH:
                    return 10;

                case Opcode.NOP:
                case Opcode.RET:
                    return 0;

                default: return 1;
            }
        }
        #endregion

        #region ORACLES
        // returns value in FIAT token
        public BigInteger GetTokenPrice(string symbol)
        {
            if (symbol == DomainSettings.FiatTokenSymbol)
            {
                return UnitConversion.GetUnitValue(DomainSettings.FiatTokenDecimals);
            }

            if (symbol == DomainSettings.FuelTokenSymbol)
            {
                var result = GetTokenPrice(DomainSettings.StakingTokenSymbol);
                result /= 5;
                return result;
            }

            Core.Throw.If(Oracle == null, "cannot read price from null oracle");

            Core.Throw.If(!Nexus.TokenExists(symbol), "cannot read price for invalid token");

            var bytes = Oracle.Read("price://" + symbol);
            var value = BigInteger.FromUnsignedArray(bytes, true);
            return value;
        }

        public BigInteger GetTokenQuote(string baseSymbol, string quoteSymbol, BigInteger amount)
        {
            if (baseSymbol == quoteSymbol)
                return amount;

            var basePrice = GetTokenPrice(baseSymbol);
            var quotePrice = GetTokenPrice(quoteSymbol);

            BigInteger result;

            var baseToken = Nexus.GetTokenInfo(baseSymbol);
            var quoteToken = Nexus.GetTokenInfo(quoteSymbol);

            result = basePrice * amount;
            result = UnitConversion.ConvertDecimals(result, baseToken.Decimals, DomainSettings.FiatTokenDecimals);

            result /= quotePrice;

            result = UnitConversion.ConvertDecimals(result, DomainSettings.FiatTokenDecimals, quoteToken.Decimals);

            return result;
        }
        #endregion

        #region RANDOM NUMBERS
        public static readonly uint RND_A = 16807;
        public static readonly uint RND_M = 2147483647;

        // returns a next random number
        public BigInteger GetRandomNumber()
        {
            if (!randomized)
            {
                // calculates first initial pseudo random number seed
                byte[] bytes = Transaction != null ? Transaction.Hash.ToByteArray() : new byte[32];

                for (int i = 0; i < this.entryScript.Length; i++)
                {
                    var index = i % bytes.Length;
                    bytes[index] ^= entryScript[i];
                }

                var time = System.BitConverter.GetBytes(Time.Value);

                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] ^= time[i % time.Length];
                }

                seed = BigInteger.FromUnsignedArray(bytes, true);
                randomized = true;
            }
            else
            {
                seed = ((RND_A * seed) % RND_M);
            }

            return seed;
        }
        #endregion

        // fetches a chain-governed value
        public BigInteger GetGovernanceValue(string name)
        {
            var value = Nexus.GetGovernanceValue(this.Chain.IsRoot ? this.ChangeSet : Nexus.RootStorage, name);
            return value;
        }

        public BigInteger GetBalance(string tokenSymbol, Address address)
        {
            var tokenInfo = Nexus.GetTokenInfo(tokenSymbol);
            if (tokenInfo.Flags.HasFlag(TokenFlags.Fungible))
            {
                var balances = new BalanceSheet(tokenSymbol);
                return balances.Get(this.ChangeSet, address);
            }
            else
            {
                var ownerships = new OwnershipSheet(tokenSymbol);
                var items = ownerships.Get(this.ChangeSet, address);
                return items.Length;
            }
        }

        public IBlock GetBlockByHash(IChain chain, Hash hash)
        {
            throw new NotImplementedException();
        }

        public IBlock GetBlockByHeight(IChain chain, BigInteger height)
        {
            throw new NotImplementedException();
        }

        public ITransaction GetTransaction(IChain chain, Hash hash)
        {
            throw new NotImplementedException();
        }

        public IToken GetToken(string symbol)
        {
            throw new NotImplementedException();
        }

        public IFeed GetFeed(string name)
        {
            throw new NotImplementedException();
        }

        public IPlatform GetPlatform(string name)
        {
            throw new NotImplementedException();
        }

        public IChain GetChainByAddress(Address address)
        {
            throw new NotImplementedException();
        }

        public IChain GetChainByName(string name)
        {
            throw new NotImplementedException();
        }

        public void Log(string description)
        {
            throw new NotImplementedException();
        }

        public void Throw(string description)
        {
            throw new NotImplementedException();
        }

        public void Notify(EventKind kind, Address address, VMObject content)
        {
            throw new NotImplementedException();
        }

        public Event GetTransactionEvents(ITransaction transaction)
        {
            throw new NotImplementedException();
        }

        public BigInteger GetBalance(IChain chain, IToken token, Address address)
        {
            throw new NotImplementedException();
        }

        internal bool TransferTokens(RuntimeVM vm, string symbol, Address source, Address destination, BigInteger amount)
        {
            var Runtime = this;
            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");
            Runtime.Expect(source != destination, "source and destination must be different");
            Runtime.Expect(Transaction.IsSignedBy(source), "invalid witness");
            Runtime.Expect(!Runtime.IsTrigger, "not allowed inside a trigger");

            if (destination.IsInterop)
            {
                Runtime.Expect(Runtime.Chain.IsRoot, "interop transfers only allowed in main chain");
                Runtime.CallContext("interop", "WithdrawTokens", source, destination, symbol, amount);
                return true;
            }

            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Transferable), "token must be transferable");

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, symbol, source, destination, amount), "transfer failed");

            Runtime.Notify(EventKind.TokenSend, source, new TokenEventData() { chainAddress = Runtime.Chain.Address, value = amount, symbol = symbol });
            Runtime.Notify(EventKind.TokenReceive, destination, new TokenEventData() { chainAddress = Runtime.Chain.Address, value = amount, symbol = symbol });

            return true;
        }

        #region TRIGGERS
        public bool InvokeTriggerOnAccount(Address address, AccountTrigger trigger, params object[] args)
        {
            if (address.IsNull)
            {
                return false;
            }

            if (address.IsUser)
            {
                var accountScript = Nexus.LookUpAddressScript(this.ChangeSet, address);
                return InvokeTrigger(accountScript, trigger.ToString(), args);
            }

            if (address.IsSystem)
            {
                var contract = Nexus.AllocContractByAddress(address);
                if (contract != null)
                {
                    var triggerName = trigger.ToString();
                    BigInteger gasCost;
                    if (contract.HasInternalMethod(triggerName, out gasCost))
                    {
                        CallContext(contract.Name, triggerName, args);
                    }
                }

                return true;
            }

            return true;
        }

        public bool InvokeTriggerOnToken(TokenInfo token, TokenTrigger trigger, params object[] args)
        {
            return InvokeTrigger(token.Script, trigger.ToString(), args);
        }

        public bool InvokeTrigger(byte[] script, string triggerName, params object[] args)
        {
            if (script == null || script.Length == 0)
            {
                return true;
            }

            var leftOverGas = (uint)(this.MaxGas - this.UsedGas);
            var runtime = new RuntimeVM(script, this.Chain, this.Time, this.Transaction, this.ChangeSet, this.Oracle, false, true);
            runtime.ThrowOnFault = true;

            for (int i = args.Length - 1; i >= 0; i--)
            {
                var obj = VMObject.FromObject(args[i]);
                runtime.Stack.Push(obj);
            }
            runtime.Stack.Push(VMObject.FromObject(triggerName));

            var state = runtime.Execute();
            // TODO catch VM exceptions?

            // propagate gas consumption
            // TODO this should happen not here but in real time during previous execution, to prevent gas attacks
            this.ConsumeGas(runtime.UsedGas);

            if (state == ExecutionState.Halt)
            {
                // propagate events to the other runtime
                foreach (var evt in runtime.Events)
                {
                    this.Notify(evt.Kind, evt.Address, evt.Data);
                }

                return true;
            }
            else
            {
                return false;
            }
        }
        #endregion

        public bool IsWitness(Address address)
        {
            if (address == this.Chain.Address /*|| address == this.Address*/)
            {
                return false;
            }

            if (address == this.EntryAddress)
            {
                return true;
            }

            if (address.IsSystem)
            {
                var contextAddress = SmartContract.GetAddressForName(this.CurrentContext.Name);
                return contextAddress == address;
            }

            if (address.IsInterop)
            {
                return false;
            }

            if (address.IsUser && this.Nexus.HasScript(ChangeSet, address))
            {
                return InvokeTriggerOnAccount(address, AccountTrigger.OnWitness, address);
            }

            if (this.Transaction == null)
            {
                return false;
            }

            return this.Transaction.IsSignedBy(address);
        }

        public IBlock GetBlockByHash(Hash hash)
        {
            throw new NotImplementedException();
        }

        public IBlock GetBlockByHeight(BigInteger height)
        {
            throw new NotImplementedException();
        }

        public ITransaction GetTransaction(Hash hash)
        {
            throw new NotImplementedException();
        }

        public IContract GetContract(string name)
        {
            throw new NotImplementedException();
        }

        public bool TokenExists(string symbol)
        {
            throw new NotImplementedException();
        }

        public bool FeedExists(string name)
        {
            throw new NotImplementedException();
        }

        public bool PlatformExists(string name)
        {
            throw new NotImplementedException();
        }

        public bool ContractExists(string name)
        {
            throw new NotImplementedException();
        }

        public bool ContractDeployed(string name)
        {
            throw new NotImplementedException();
        }

        public bool ArchiveExists(Hash hash)
        {
            throw new NotImplementedException();
        }

        public IArchive GetArchive(Hash hash)
        {
            throw new NotImplementedException();
        }

        public bool DeleteArchive(Hash hash)
        {
            throw new NotImplementedException();
        }

        public bool ChainExists(string name)
        {
            throw new NotImplementedException();
        }

        public int GetIndexOfChain(string name)
        {
            throw new NotImplementedException();
        }

        public IChain GetChainParent(string name)
        {
            throw new NotImplementedException();
        }

        public Address LookUpName(string name)
        {
            throw new NotImplementedException();
        }

        public bool HasAddressScript(Address from)
        {
            throw new NotImplementedException();
        }

        public byte[] GetAddressScript(Address from)
        {
            throw new NotImplementedException();
        }

        Event[] IRuntime.GetTransactionEvents(ITransaction transaction)
        {
            throw new NotImplementedException();
        }

        public Address GetValidatorForBlock(Hash hash)
        {
            throw new NotImplementedException();
        }

        public ValidatorEntry GetValidatorByIndex(int index)
        {
            throw new NotImplementedException();
        }

        public ValidatorEntry[] GetValidators()
        {
            throw new NotImplementedException();
        }

        public bool IsPrimaryValidator(Address address)
        {
            throw new NotImplementedException();
        }

        public bool IsSecondaryValidator(Address address)
        {
            throw new NotImplementedException();
        }

        public int GetPrimaryValidatorCount()
        {
            throw new NotImplementedException();
        }

        public int GetSecondaryValidatorCount()
        {
            throw new NotImplementedException();
        }

        public bool IsKnownValidator(Address address)
        {
            throw new NotImplementedException();
        }

        public bool IsStakeMaster(Address address)
        {
            throw new NotImplementedException();
        }

        public BigInteger GetStake(Address address)
        {
            throw new NotImplementedException();
        }

        public BigInteger GenerateUID()
        {
            throw new NotImplementedException();
        }

        public BigInteger GenerateRandomNumber()
        {
            throw new NotImplementedException();
        }

        public BigInteger[] GetOwnerships(string symbol, Address address)
        {
            throw new NotImplementedException();
        }

        public BigInteger GetTokenSupply(string symbol)
        {
            throw new NotImplementedException();
        }

        public bool CreateToken(string symbol, string name, string platform, Hash hash, BigInteger maxSupply, int decimals, TokenFlags flags, byte[] script)
        {
            throw new NotImplementedException();
        }

        public bool CreateChain(Address owner, string name, string parentChain, string[] contractNames)
        {
            throw new NotImplementedException();
        }

        public bool CreateFeed(Address owner, string name, FeedMode mode)
        {
            throw new NotImplementedException();
        }

        public bool CreatePlatform(Address address, string name, string fuelSymbol)
        {
            throw new NotImplementedException();
        }

        public bool CreateArchive(MerkleTree merkleTree, BigInteger size, Domain.ArchiveFlags flags, byte[] key)
        {
            throw new NotImplementedException();
        }

        public bool IsAddressOfParentChain(Address address)
        {
            throw new NotImplementedException();
        }

        public bool IsAddressOfChildChain(Address address)
        {
            throw new NotImplementedException();
        }

        public bool MintTokens(string symbol, Address target, BigInteger amount, bool isSettlement)
        {
            throw new NotImplementedException();
        }

        public bool MintToken(string symbol, Address target, BigInteger tokenID, bool isSettlement)
        {
            throw new NotImplementedException();
        }

        public bool BurnTokens(string symbol, Address target, BigInteger amount, bool isSettlement)
        {
            throw new NotImplementedException();
        }

        public bool BurnToken(string symbol, Address target, BigInteger tokenID, bool isSettlement)
        {
            throw new NotImplementedException();
        }

        public bool TransferTokens(string symbol, Address source, Address destination, BigInteger amount)
        {
            throw new NotImplementedException();
        }

        public bool TransferToken(string symbol, Address source, Address destination, BigInteger tokenID)
        {
            throw new NotImplementedException();
        }

        public bool SendTokens(Address targetChainAddress, Address from, Address to, string symbol, BigInteger amount)
        {
            throw new NotImplementedException();
        }

        public bool SendToken(Address targetChainAddress, Address from, Address to, string symbol, BigInteger tokenID)
        {
            throw new NotImplementedException();
        }

        public BigInteger CreateNFT(string tokenSymbol, Address chainAddress, byte[] rom, byte[] ram)
        {
            throw new NotImplementedException();
        }

        public bool DestroyNFT(string tokenSymbol, BigInteger tokenID)
        {
            throw new NotImplementedException();
        }

        public bool EditNFTContent(string tokenSymbol, BigInteger tokenID, byte[] ram)
        {
            throw new NotImplementedException();
        }

        public TokenContent GetNFT(string tokenSymbol, BigInteger tokenID)
        {
            throw new NotImplementedException();
        }

        public byte[] ReadOracle(string URL)
        {
            return this.Oracle.Read(URL);
        }
    }
}
