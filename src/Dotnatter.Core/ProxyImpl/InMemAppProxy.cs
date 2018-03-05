﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dotnatter.Core.Crypto;
using Dotnatter.Core.HashgraphImpl.Model;
using Nito.AsyncEx;
using Serilog;

namespace Dotnatter.Core.ProxyImpl
{
    public class InMemAppProxy : IAppProxy
    {
        private readonly AsyncProducerConsumerQueue<byte[]> submitCh;
        private readonly List<byte[]> committedTransactions;
        private readonly ILogger logger;
        private byte[] stateHash;

        public InMemAppProxy(ILogger logger)
        {
            submitCh = new AsyncProducerConsumerQueue<byte[]>();
            committedTransactions = new List<byte[]>();
            this.logger = logger;
        }

        public AsyncProducerConsumerQueue<byte[]> SubmitCh()
        {
            return submitCh;
        }

        private (byte[] stateHash, ProxyError err) Commit(Block block)

        {
            committedTransactions.AddRange(block.Transactions());

            var hash = stateHash.ToArray();
            foreach (var t in block.Transactions())
            {
                var tHash = CryptoUtils.Sha256(t);
                hash = Hash.SimpleHashFromTwoHashes(hash, tHash);
            }

            stateHash = hash;

            return (stateHash, null);
        }

        public (byte[] stateHash, ProxyError err) CommitBlock(Block block)
        {
            logger.Debug("InmemProxy CommitBlock RoundReceived={RoundReceived}; TxCount={TxCount}", block.RoundReceived());
            return Commit(block);
        }

//-------------------------------------------------------
//Implement AppProxy Interface

        public async Task SubmitTx(byte[] tx)
        {
            await submitCh.EnqueueAsync(tx);
        }

        public byte[][] GetCommittedTransactions()
        {
            return committedTransactions.ToArray();
        }
    }
}