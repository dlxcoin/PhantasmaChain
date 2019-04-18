﻿using System;
using System.Collections.Generic;
using LunarLabs.Parser;
using LunarLabs.Parser.JSON;
using Phantasma.Cryptography;

namespace Phantasma.Pay
{
    public struct CryptoCurrencyInfo
    {
        public readonly string Symbol;
        public readonly string Name;
        public readonly int Decimals;
        public readonly WalletKind Kind;

        public CryptoCurrencyInfo(string symbol, string name, int decimals, WalletKind kind)
        {
            Symbol = symbol;
            Name = name;
            Decimals = decimals;
            Kind = kind;
        }
    }

    public abstract class CryptoWallet
    {
        public abstract WalletKind Kind { get; }
        public readonly string Address;

        protected List<WalletBalance> _balances = new List<WalletBalance>();
        public IEnumerable<WalletBalance> Balances => _balances;
        
        public CryptoWallet(KeyPair keys)
        {
            this.Address = DeriveAddress(keys);
        }

        protected abstract string DeriveAddress(KeyPair keys);

        public abstract void SyncBalances(Action<bool> callback);
        public abstract void MakePayment(string symbol, decimal amount, string targetAddress, Action<bool> callback);

        public abstract IEnumerable<CryptoCurrencyInfo> GetCryptoCurrencyInfos();

        protected void JSONRequest(string url, Action<DataNode> callback)
        {
            string contents;
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    contents = wc.DownloadString(url);
                    var root = JSONReader.ReadFromString(contents);
                    callback(root);
                    return;
                }
            }
            catch (Exception e)
            {                
                callback(null);
            }
        }
    }
}
