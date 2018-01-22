﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Dotnatter.Crypto;
using Dotnatter.HashgraphImpl;
using Dotnatter.HashgraphImpl.Model;
using Dotnatter.NodeImpl;
using Dotnatter.Test.Helpers;
using Dotnatter.Util;
using Serilog;
using Serilog.Events;
using Xunit;
using Xunit.Abstractions;

namespace Dotnatter.Test.NodeImpl
{
    public class CoreTests
    {
        private readonly ITestOutputHelper output;
        private readonly ILogger logger;

        public CoreTests(ITestOutputHelper output)
        {
            this.output = output;
            logger = output.SetupLogging().ForContext("SourceContext", "HashGraphTests");
        }

        [Fact]
        public void TestInit()
        {
            var key = CryptoUtils.GenerateEcdsaKey();

            var participants = new Dictionary<string, int> {{CryptoUtils.FromEcdsaPub(key).ToHex(), 0}};
            var core = new Core(0, key, participants, new InmemStore(participants, 10, logger), null, logger);

            var err = core.Init();

            Assert.Null(err);
        }

        private (Core[] cores, CngKey[] privateKey, Dictionary<string, string> index) InitCores(int n)
        {
            var cacheSize = 1000;

            var cores = new List<Core>();
            var index = new Dictionary<string, string>();

            var participantKeys = new List<CngKey>();
            var participants = new Dictionary<string, int>();
            for (var i = 0; i < n; i++)
            {
                var key = CryptoUtils.GenerateEcdsaKey();
                participantKeys.Add(key);
                participants[CryptoUtils.FromEcdsaPub(key).ToHex()] = i;
            }

            for (var i = 0; i < n; i++)
            {
                var core = new Core(i, participantKeys[i], participants, new InmemStore(participants, cacheSize, logger), null, logger);
                core.Init();
                cores.Add(core);

                index[$"e{i}"] = core.Head;
            }

            return (cores.ToArray(), participantKeys.ToArray(), index);
        }

        [Fact]
        public void TestInitCores()
        {
            var (cores, privateKey, index) = InitCores(3);
            Assert.NotEmpty(cores);
            Assert.NotEmpty(privateKey);
            Assert.NotEmpty(index);
        }

        /*
        |  e12  |
        |   | \ |
        |   |   e20
        |   | / |
        |   /   |
        | / |   |
        e01 |   |
        | \ |   |
        e0  e1  e2
        0   1   2
        */
        private void InitHashgraph(Core[] cores, CngKey[] keys, Dictionary<string, string> index, int participant)

        {
            Exception err;
            for (var i = 0; i < cores.Length; i++)
            {
                if (i != participant)
                {
                    var evh = index[$"e{i}"];

                    var ( ev, _) = cores[i].GetEvent(evh);

                    err = cores[participant].InsertEvent(ev, true);

                    if (err != null)
                    {
                        output.WriteLine("error inserting {0}: {1} ", GetName(index, ev.Hex()), err);
                    }
                }
            }

            var event01 = new Event(new byte[][] { },
                new[] {index["e0"], index["e1"]}, //e0 and e1
                cores[0].PubKey(), 1);

            err = InsertEvent(cores, keys, index, event01, "e01", participant, 0);
            if (err != null)
            {
                output.WriteLine("error inserting e01: {0}", err);
            }

            var event20 = new Event(new byte[][] { },
                new[] {index["e2"], index["e01"]}, //e2 and e01
                cores[2].PubKey(), 1);

            err = InsertEvent(cores, keys, index, event20, "e20", participant, 2);
            if (err != null)
            {
                output.WriteLine("error inserting e20: {0}", err);
            }

            var event12 = new Event(new byte[][] { },
                new[] {index["e1"], index["e20"]}, //e1 and e20
                cores[1].PubKey(), 1);

            err = InsertEvent(cores, keys, index, event12, "e12", participant, 1);

            if (err != null)
            {
                output.WriteLine("error inserting e12: {err}", err);
            }
        }

        public Exception InsertEvent(Core[] cores, CngKey[] keys, Dictionary<string, string> index, Event ev, string name, int particant, int creator)
        {
            Exception err;
            if (particant == creator)
            {
                err = cores[particant].SignAndInsertSelfEvent(ev);

                if (err != null)
                {
                    return err;
                }

                //event is not signed because passed by value
                index[name] = cores[particant].Head;
            }
            else
            {
                ev.Sign(keys[creator]);

                err = cores[particant].InsertEvent(ev, true);

                if (err != null)
                {
                    return err;
                }

                index[name] = ev.Hex();
            }

            return null;
        }

        [Fact]
        public void TestDiff()
        {
            var (cores, keys, index) = InitCores(3);

            InitHashgraph(cores, keys, index, 0);

            /*
               P0 knows
        
               |  e12  |
               |   | \ |
               |   |   e20
               |   | / |
               |   /   |
               | / |   |
               e01 |   |        P1 knows
               | \ |   |
               e0  e1  e2       |   e1  |
               0   1   2        0   1   2
            */

            var knownBy1 = cores[1].Known();
            var (unknownBy1, err) = cores[0].Diff(knownBy1);

            Assert.Null(err);
            var l = unknownBy1.Length;

            Assert.Equal(5, l);

            var expectedOrder = new[] {"e0", "e2", "e01", "e20", "e12"};

            var i = 0;
            foreach (var e in unknownBy1)
            {
                var name = GetName(index, e.Hex());
                Assert.True(name == expectedOrder[i], $"element {i} should be {expectedOrder[i]}, not {name}");
                i++;
            }
        }

        [Fact]
        public void TestSync()

        {
            var (cores, _, index) = InitCores(3);

            /*
               core 0           core 1          core 2
        
               e0  |   |        |   e1  |       |   |   e2
               0   1   2        0   1   2       0   1   2
            */

            //core 1 is going to tell core 0 everything it knows

            var err = SynchronizeCores(cores, 1, 0, new byte[][] { });

            Assert.Null(err);

            /*
               core 0           core 1          core 2
        
               e01 |   |
               | \ |   |
               e0  e1  |        |   e1  |       |   |   e2
               0   1   2        0   1   2       0   1   2
            */

            var knownBy0 = cores[0].Known();

            var k = knownBy0[cores[0].Id()];
            Assert.False(k != 1, "core 0 should have last-index 1 for core 0, not {k}");

            k = knownBy0[cores[1].Id()];
            Assert.False(k != 0, "core 0 should have last-index 0 for core 1, not {k}");

            k = knownBy0[cores[2].Id()];

            Assert.False(k != -1, "core 0 should have last-index -1 for core 2, not {k}");

            var (core0Head, _ ) = cores[0].GetHead();

            Assert.False(core0Head.SelfParent != index["e0"], "core 0 head self-parent should be e0");

            Assert.False(core0Head.OtherParent != index["e1"], "core 0 head other-parent should be e1");

            index["e01"] = core0Head.Hex();

            //core 0 is going to tell core 2 everything it knows
            err = SynchronizeCores(cores, 0, 2, new byte[][] { });

            Assert.Null(err);

            /*
        
               core 0           core 1          core 2
        
                                                |   |  e20
                                                |   | / |
                                                |   /   |
                                                | / |   |
               e01 |   |                        e01 |   |
               | \ |   |                        | \ |   |
               e0  e1  |        |   e1  |       e0  e1  e2
               0   1   2        0   1   2       0   1   2
            */

            var knownBy2 = cores[2].Known();

            k = knownBy2[cores[0].Id()];
            Assert.False(k != 1, "core 2 should have last-index 1 for core 0, not {k}");

            k = knownBy2[cores[1].Id()];
            Assert.False(k != 0, "core 2 should have last-index 0 core 1, not {k}");

            k = knownBy2[cores[2].Id()];
            Assert.False(k != 1, "core 2 should have last-index 1 for core 2, not {k}");

            var (core2Head, _) = cores[2].GetHead();

            Assert.Equal(index["e2"], core2Head.SelfParent); // core 2 head self-parent should be e2
            Assert.Equal(index["e01"], core2Head.OtherParent); // core 2 head other-parent should be e01

            index["e20"] = core2Head.Hex();

            //core 2 is going to tell core 1 everything it knows
            err = SynchronizeCores(cores, 2, 1, new byte[][] { });

            Assert.Null(err);

            /*
        
               core 0           core 1          core 2
        
                                |  e12  |
                                |   | \ |
                                |   |  e20      |   |  e20
                                |   | / |       |   | / |
                                |   /   |       |   /   |
                                | / |   |       | / |   |
               e01 |   |        e01 |   |       e01 |   |
               | \ |   |        | \ |   |       | \ |   |
               e0  e1  |        e0  e1  e2      e0  e1  e2
               0   1   2        0   1   2       0   1   2
            */

            var knownBy1 = cores[1].Known();
            k = knownBy1[cores[0].Id()];

            Assert.False(k != 1, "core 1 should have last-index 1 for core 0, not {k}");

            k = knownBy1[cores[1].Id()];

            Assert.False(k != 1, "core 1 should have last-index 1 for core 1, not {k}");

            k = knownBy1[cores[2].Id()];

            Assert.False(k != 1, "core 1 should have last-index 1 for core 2, not {k}");

            var (core1Head, _) = cores[1].GetHead();
            Assert.False(core1Head.SelfParent != index["e1"], "core 1 head self-parent should be e1");

            Assert.False(core1Head.OtherParent != index["e20"], "core 1 head other-parent should be e20");

            index["e12"] = core1Head.Hex();
        }

/*
h0  |   h2
| \ | / |
|   h1  |
|  /|   |--------------------
g02 |   | R2
| \ |   |
|   \   |
|   | \ |
|   |  g21
|   | / |
|  g10  |
| / |   |
g0  |   g2
| \ | / |
|   g1  |
|  /|   |--------------------
f02 |   | R1
| \ |   |
|   \   |
|   | \ |
|   |  f21
|   | / |
|  f10  |
| / |   |
f0  |   f2
| \ | / |
|   f1  |
|  /|   |--------------------
e02 |   | R0 Consensus
| \ |   |
|   \   |
|   | \ |
|   |  e21
|   | / |
|  e10  |
| / |   |
e0  e1  e2
0   1    2
*/
        public class Play
        {
            public int From { get; }
            public int To { get; }
            public byte[][] Payload { get; }

            public Play(int from, int to, byte[][] payload)
            {
                From = from;
                To = to;
                Payload = payload;
            }
        }

        private Core[] InitConsensusHashgraph()
        {
            var (cores, _, _) = InitCores(3);
            var playbook = new[]
            {
                new Play(0, 1, new[] {"e10".StringToBytes()}),
                new Play(1, 2, new[] {"e21".StringToBytes()}),
                new Play(2, 0, new[] {"e02".StringToBytes()}),
                new Play(0, 1, new[] {"f1".StringToBytes()}),
                new Play(1, 0, new[] {"f0".StringToBytes()}),
                new Play(1, 2, new[] {"f2".StringToBytes()}),

                new Play(0, 1, new[] {"f10".StringToBytes()}),
                new Play(1, 2, new[] {"f21".StringToBytes()}),
                new Play(2, 0, new[] {"f02".StringToBytes()}),
                new Play(0, 1, new[] {"g1".StringToBytes()}),
                new Play(1, 0, new[] {"g0".StringToBytes()}),
                new Play(1, 2, new[] {"g2".StringToBytes()}),

                new Play(0, 1, new[] {"g10".StringToBytes()}),
                new Play(1, 2, new[] {"g21".StringToBytes()}),
                new Play(2, 0, new[] {"g02".StringToBytes()}),
                new Play(0, 1, new[] {"h1".StringToBytes()}),
                new Play(1, 0, new[] {"h0".StringToBytes()}),
                new Play(1, 2, new[] {"h2".StringToBytes()})
            };

            foreach (var play in playbook)
            {
                var err = SyncAndRunConsensus(cores, play.From, play.To, play.Payload);

                Assert.Null(err);
            }

            return cores;
        }

        [Fact]
       public void TestConsensus()
        {
            var cores = InitConsensusHashgraph();

            var l = cores[0].GetConsensusEvents().Length;
           
            Assert.Equal(6,l); //length of consensus should be 6

            var core0Consensus = cores[0].GetConsensusEvents();
            var core1Consensus = cores[1].GetConsensusEvents();
            var core2Consensus = cores[2].GetConsensusEvents();

            //for (var i = 0; i < l; i++)
            //{
            //    output.WriteLine("{0}: {1}, {2}, {3}", i ,core0Consensus[i],core1Consensus[i],core2Consensus[i] );
            //}

            for (var i=0; i<l; i++)


            {
                var e=core0Consensus[i];

                Assert.Equal(e,core1Consensus[i]); //core 1 consensus[%d] does not match core 0's
                Assert.Equal(e,core2Consensus[i]); //core 2 consensus[%d] does not match core 0's
          
            }
        }


        [Fact]
        public void TestOverSyncLimit()
        {
            var cores = InitConsensusHashgraph();

            var known = new Dictionary<int, int> ();

            var syncLimit = 10;

            //positive
            for (var i = 0; i < 3; i++)
            {
                known[i] = 1;
            }

            Assert.True(cores[0].OverSyncLimit(known, syncLimit), $"OverSyncLimit({known}, {syncLimit}) should return true");
     
            //negative
            for (var i = 0; i < 3; i++)
            {
                known[i] = 6;
            }

            Assert.False(cores[0].OverSyncLimit(known, syncLimit), $"OverSyncLimit({known}, {syncLimit}) should return false");
            
            //edge
            known = new Dictionary<int, int>()
            {
                {0, 2},
                {1, 3},
                {2, 3},
            };

            Assert.False(cores[0].OverSyncLimit(known, syncLimit), $"OverSyncLimit({known}, {syncLimit}) should return false");
            
        }

        

        private Exception SynchronizeCores(Core[] cores, int from, int to, byte[][] payload)
        {
            var knownByTo = cores[to].Known();
            var ( unknownByTo, err) = cores[from].Diff(knownByTo);
            if (err != null)
            {
                return err;
            }

            WireEvent[] unknownWire;
            ( unknownWire, err) = cores[from].ToWire(unknownByTo);
            if (err != null)
            {
                return err;
            }

            cores[to].AddTransactions(payload);

            //output.WriteLine($"From: {from}; To: {to}");
            //output.WriteLine(unknownWire.DumpToString());

            return cores[to].Sync(unknownWire);
        }

        private Exception SyncAndRunConsensus(Core[] cores, int from, int to, byte[][] payload)
        {
            var err = SynchronizeCores(cores, from, to, payload);

            if (err != null)
            {
                return err;
            }

            cores[to].RunConsensus();
            return null;
        }

        private string GetName(Dictionary<string, string> index, string hash)
        {
            foreach (var i in index)
            {
                var name = i.Key;
                var h = i.Value;
                if (h == hash)
                {
                    return name;
                }
            }

            return $"{hash} not found";
        }
    }
}