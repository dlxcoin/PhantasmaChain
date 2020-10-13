﻿using Phantasma.Domain;
using Phantasma.Core;
using Phantasma.VM;

namespace Phantasma.Blockchain
{
    public sealed class CustomContract : SmartContract
    {
        private string _name;
        public override string Name => _name;

        public byte[] Script { get; private set; }

        public CustomContract(string name, byte[] script, ContractInterface abi) : base()
        {
            Throw.IfNull(script, nameof(script));
            this.Script = script;

            _name = name;

            this.ABI = abi;
        }
    }
}
