﻿using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using NativeBigInt = System.Numerics.BigInteger; // hack to overcome Phantasma.Numerics.BigInteger
using Phantasma.Storage;
using Phantasma.Storage.Utils;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace Phantasma.Blockchain
{
    public struct OracleFeed: IFeed, ISerializable
    {
        public string Name { get; private set; }
        public Address Address { get; private set; }
        public FeedMode Mode { get; private set; }

        public OracleFeed(string name, Address address, FeedMode mode)
        {
            Name = name;
            Address = address;
            Mode = mode;
        }

        public void SerializeData(BinaryWriter writer)
        {
            writer.WriteVarString(Name);
            writer.WriteAddress(Address);
            writer.Write((byte)Mode);
        }

        public void UnserializeData(BinaryReader reader)
        {
            Name = reader.ReadVarString();
            Address = reader.ReadAddress();
            Mode = (FeedMode)reader.ReadByte();
        }

        public byte[] ToByteArray()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    SerializeData(writer);
                }

                return stream.ToArray();
            }
        }

        public static OracleFeed Unserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                using (var reader = new BinaryReader(stream))
                {
                    var entity = new OracleFeed();
                    entity.UnserializeData(reader);
                    return entity;
                }
            }
        }
    }

    public struct OracleEntry: IOracleEntry
    {
        public string URL { get; private set; }
        public byte[] Content { get; private set; }

        public OracleEntry(string uRL, byte[] content)
        {
            URL = uRL;
            Content = content;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is OracleEntry))
            {
                return false;
            }

            var entry = (OracleEntry)obj;
            return URL == entry.URL &&
                   EqualityComparer<object>.Default.Equals(Content, entry.Content);
        }

        public override int GetHashCode()
        {
            var hashCode = 1993480784;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(URL);
            hashCode = hashCode * -1521134295 + EqualityComparer<object>.Default.GetHashCode(Content);
            return hashCode;
        }
    }

    public abstract class OracleReader
    {
        public const string interopTag = "interop://";
        public const string priceTag = "price://";

        private ConcurrentDictionary<string, OracleEntry> _entries = new ConcurrentDictionary<string, OracleEntry>();

        public IEnumerable<OracleEntry> Entries => _entries.Values;

        protected abstract T PullData<T>(Timestamp time, string url);
        protected abstract decimal PullPrice(Timestamp time, string symbol);
        protected abstract InteropBlock PullPlatformBlock(string platformName, string chainName, Hash hash, NativeBigInt height = new NativeBigInt());
        protected abstract InteropTransaction PullPlatformTransaction(string platformName, string chainName, Hash hash);
        public abstract string GetCurrentHeight(string platformName, string chainName);
        public abstract void SetCurrentHeight(string platformName, string chainName, string height);
        public abstract List<InteropBlock> ReadAllBlocks(string platformName, string chainName);

        public readonly Nexus Nexus;

        public OracleReader(Nexus nexus)
        {
            this.Nexus = nexus;
        }

        public T Read<T>(Timestamp time, string url)where T : class 
        {
            if (_entries.ContainsKey(url))
            {
                return (_entries[url].Content) as T;
            }

            T content;

            if (url.StartsWith(interopTag))
            {
                url = url.Substring(interopTag.Length);
                var args = url.Split('/');

                var platformName = args[0];
                var chainName = args[1];
                if (Nexus.PlatformExists(Nexus.RootStorage, platformName))
                {
                    args = args.Skip(2).ToArray();
                    content = ReadChainOracle<T>(platformName, chainName, args);
                }
                else
                { 
                    throw new OracleException("invalid oracle platform: " + platformName);
                }
            }
            else
            if (url.StartsWith(priceTag))
            {
                url = url.Substring(priceTag.Length);

                if (url.Contains('/'))
                {
                    throw new OracleException("invalid oracle price request");
                }

                var baseSymbol = url;

                if (!Nexus.TokenExists(Nexus.RootStorage, baseSymbol))
                {
                    throw new OracleException("unknown token: " + baseSymbol);
                }

                var price = PullPrice(time, baseSymbol);
                var val = UnitConversion.ToBigInteger(price, DomainSettings.FiatTokenDecimals);
                content = val.ToUnsignedByteArray() as T;
            }
            else
            {
                content = PullData<T>(time, url);
            }
        
            var entry = new OracleEntry(url, Serialization.Serialize(content));
            lock (_entries)
            {
                _entries[url] = entry;
            }

            return content;
        }

        private bool FindMatchingEvent(IEnumerable<Event> events, out Event output, Func<Event, bool> predicate)
        {
            foreach (var evt in events)
            {
                if (predicate(evt))
                {
                    output = evt;
                    return true;
                }
            }

            output = new Event();
            return false;
        }

        private T ReadChainOracle<T>(string platformName, string chainName, string[] input) where T : class
        {
            if (input == null || input.Length != 2)
            {
                throw new OracleException("missing oracle input");
            }

            var cmd = input[0].ToLower();
            switch (cmd)
            {
                case "tx":
                case "transaction":
                    {
                        Hash hash;
                        if (Hash.TryParse(input[1], out hash))
                        {
                            InteropTransaction tx;

                            if (platformName == DomainSettings.PlatformName)
                            {
                                var chain = Nexus.GetChainByName(chainName);

                                var blockHash = chain.GetBlockHashOfTransaction(hash);
                                var block = chain.GetBlockByHash(blockHash);

                                var temp = chain.GetTransactionByHash(hash);
                                if (block == null || temp == null)
                                {
                                    throw new OracleException($"invalid transaction hash for chain {chainName} @ {platformName}");
                                }

                                var events = block.GetEventsForTransaction(hash);
                                var transfers = new List<InteropTransfer>();
                                foreach (var evt in events)
                                {
                                    switch (evt.Kind)
                                    {
                                        case EventKind.TokenSend:
                                            {
                                                var data = evt.GetContent<TokenEventData>();
                                                Event other;
                                                if (FindMatchingEvent(events, out other,
                                                    (x) =>
                                                    {
                                                        if (x.Kind != EventKind.TokenReceive && x.Kind != EventKind.TokenStake)
                                                        {
                                                            return false;
                                                        }

                                                        var y = x.GetContent<TokenEventData>();
                                                        return y.Symbol == data.Symbol && y.Value == data.Value;
                                                    }))
                                                {
                                                    var otherData = other.GetContent<TokenEventData>();

                                                    byte[] rawData = null;

                                                    var token = Nexus.GetTokenInfo(Nexus.RootStorage, data.Symbol);
                                                    if (!token.IsFungible())
                                                    {
                                                        Event nftEvent;
                                                        if (!FindMatchingEvent(events, out nftEvent,
                                                            (x) =>
                                                            {
                                                                if (x.Kind != EventKind.PackedNFT)
                                                                {
                                                                    return false;
                                                                }

                                                                var y = x.GetContent<PackedNFTData>();
                                                                return y.Symbol == data.Symbol;
                                                            }))
                                                        {
                                                            throw new OracleException($"invalid nft transfer with hash in chain {chainName} @ {platformName}");
                                                        }

                                                        rawData = nftEvent.Data;
                                                    }

                                                    transfers.Add(new InteropTransfer(data.ChainName, evt.Address, otherData.ChainName, other.Address, Address.Null, data.Symbol, data.Value, rawData));
                                                }
                                                break;
                                            }
                                    }
                                }

                                tx = new InteropTransaction(hash, transfers);
                                if (typeof(T) == typeof(byte[]))
                                {
                                    return Serialization.Serialize(tx) as T;
                                }
                            }
                            else
                            {
                                tx = PullPlatformTransaction(platformName, chainName, hash);
                            }

                            if (typeof(T) == typeof(byte[]))
                            {
                                return Serialization.Serialize(tx) as T;
                            }

                            return tx as T;
                        }
                        else
                        {
                            throw new OracleException($"invalid transaction hash for chain {chainName} @ {platformName}");
                        }
                    }

                case "block":
                    {
                        Hash hash;
                        InteropBlock block;
                        NativeBigInt height;
                        // if it fails it might be block height
                        if (Hash.TryParse(input[1], out hash))
                        {
                            if (platformName == DomainSettings.PlatformName)
                            {
                                var chain = Nexus.GetChainByName(chainName);
                                var temp = chain.GetBlockByHash(hash);
                                if (temp == null)
                                {
                                    throw new OracleException($"invalid block hash for chain {chainName} @ {platformName}");
                                }

                                block = new InteropBlock(platformName, chainName, hash, temp.TransactionHashes);
                            }
                            else
                            {
                                block = PullPlatformBlock(platformName, chainName, hash);
                            }

                            if (typeof(T) == typeof(byte[]))
                            {
                                return Serialization.Serialize(block) as T;
                            }

                            return (block) as T;
                        }
                        //TODO
                        else if (NativeBigInt.TryParse(input[1], out height))
                        {
                            if (platformName == DomainSettings.PlatformName)
                            {
                                var chain = Nexus.GetChainByName(chainName);
                                var temp = chain.GetBlockByHash(hash);
                                if (temp == null)
                                {
                                    throw new OracleException($"invalid block hash for chain {chainName} @ {platformName}");
                                }

                                block = new InteropBlock(platformName, chainName, hash, temp.TransactionHashes);
                            }
                            else
                            {
                                block = PullPlatformBlock(platformName, chainName, Hash.Null, height);
                            }

                            if (typeof(T) == typeof(byte[]))
                            {
                                return Serialization.Serialize(block) as T;
                            }

                            return (block) as T;

                        }
                        else
                        {
                            throw new OracleException($"invalid block hash for chain {chainName} @ {platformName}");
                        }
                    }

                default:
                    throw new OracleException("unknown platform oracle");
            }
        }

        public InteropTransaction ReadTransaction(string platform, string chain, Hash hash)
        {
            var url = DomainExtensions.GetOracleTransactionURL(platform, chain, hash);
            var bytes = this.Read<InteropTransaction>(Timestamp.Now, url);
            return bytes;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
