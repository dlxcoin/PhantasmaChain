﻿using System;
using System.Collections.Generic;
using System.Numerics;
using Phantasma.VM.Types;

namespace Phantasma.Blockchain.Consensus
{
    public class ProofOfWork
    {
        public static Block MineBlock(Chain chain, byte[] minerPublicKey, IEnumerable<Transaction> txs)
        {
            var timestamp = Timestamp.Now;
            var block = new Block(timestamp, minerPublicKey, chain.NativeTokenPubKey, txs, chain.lastBlock);

            BigInteger target = 0;
            for (int i = 0; i <= block.difficulty; i++)
            {
                BigInteger k = 1;
                k <<= i;
                target += k;
            }

            do
            {
                var n = new BigInteger(block.Hash);
                if (n < target)
                {
                    break;
                }

                block.UpdateHash(block.Nonce + 1);
            } while (true);

            return block;
        }
    }
}