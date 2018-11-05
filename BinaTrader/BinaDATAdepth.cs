using BinanceExchange.API.Models.Response;
using BinanceExchange.API.Models.WebSocket;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinaTrader
{
    class BinaDataDepth
    {
        public ConcurrentDictionary<string, DepthCacheObject> OrderBooks { get; private set; }
        private Dictionary<string, long> UpdateIDs;
        public ConcurrentQueue<BinanceDepthData> DepthUpdateQueue { get; private set; }
        
        static readonly ILog Log = LogManager.GetLogger(typeof(BinaDataDepth));

        public BinaDataDepth()
        {
            OrderBooks = new ConcurrentDictionary<string, DepthCacheObject>();
            DepthUpdateQueue = new ConcurrentQueue<BinanceDepthData>();
            UpdateIDs = new Dictionary<string, long>();
        }

        public void StartUpdates()
        {
            //Begin Dequeue Thread:
            var DequeueThread = new Thread(() => ProcessQueue());
            DequeueThread.IsBackground = true;
            DequeueThread.Name = "UserData-Dequeue-Thread";
            DequeueThread.Start();
        }

        private void ProcessQueue()
        {
            while (true)
            {
                // Pause if no pending updates are available:
                if (DepthUpdateQueue.IsEmpty)
                {
                    Thread.Sleep(100);
                    continue;
                }

                // DeQueue next DepthUpdate:
                bool tryDQ = false;
                var DepthMsg = new BinanceDepthData();
                do { tryDQ = DepthUpdateQueue.TryDequeue(out DepthMsg); } while (!tryDQ);
                
                // Validate sequential nonce:
                if (!UpdateIDs.ContainsKey(DepthMsg.Symbol) || DepthMsg.UpdateId <= UpdateIDs[DepthMsg.Symbol])
                    continue;

                /// THIS PART RELOADS  DEPTH BECAUSE OF MISSING firstUpdateID field. Not shown to be necessary,
                //else if (DepthMsg.singleDepth.UpdateId > UpdateIDs[DepthMsg.singleDepth.Symbol] + DepthMsg.singleDepth.BidDepthDeltas.Count + DepthMsg.singleDepth.AskDepthDeltas.Count)
                //{
                //    // IF NONCE IS DE-SYNCED, WIPE BOOK AND RE-BUILD
                //    //Log.Info(string.Format("    !!!!ERR>>  NONCE OUT OF ORDER! [" + DepthMsg.singleDepth.Symbol + "] BOOK-DSYNC.  {0} -> {1}", 
                //    //   UpdateIDs[DepthMsg.singleDepth.Symbol], DepthMsg.singleDepth.UpdateId));

                //    BuildDepthCache(DepthMsg.singleDepth.Symbol).Wait();
                //    //Log.Info("    {["+ DepthMsg.singleDepth.Symbol +"] BOOK RE-SYNCED}");
                //    continue;
                //}
                else
                {
                    // UPDATE ORDER BOOK:
                    DepthMsg.BidDepthDeltas.ForEach((bd) => { CorrectlyUpdateDepthCache(bd, OrderBooks[DepthMsg.Symbol].Bids); });
                    DepthMsg.AskDepthDeltas.ForEach((ad) => { CorrectlyUpdateDepthCache(ad, OrderBooks[DepthMsg.Symbol].Asks); });                    
                    UpdateIDs[DepthMsg.Symbol] = DepthMsg.UpdateId;
                }   
                                
            }
        }
       
        
        private void CorrectlyUpdateDepthCache(TradeResponse bd, Dictionary<decimal, decimal> depthCache)
        {
            const decimal defaultIgnoreValue = 0.00000000M;

            if (depthCache.ContainsKey(bd.Price))
            {
                if (bd.Quantity == defaultIgnoreValue)
                    depthCache.Remove(bd.Price);
                else
                    depthCache[bd.Price] = bd.Quantity;                
            }
            else
            {
                if (bd.Quantity != defaultIgnoreValue)
                    depthCache[bd.Price] = bd.Quantity;
            }
        }
        

        public async Task BuildDepthCache(string symbol)
        {

            // Code example of building out a Dictionary local cache for a symbol using deltas from the WebSocket
            var localDepthCache = new Dictionary<string, DepthCacheObject> {{ symbol, new DepthCacheObject()
            {
                Asks = new Dictionary<decimal, decimal>(),
                Bids = new Dictionary<decimal, decimal>(),
            }}};
            var marketDepthCache = localDepthCache[symbol];

            // Get Order Book, and use Cache
            var depthResults = await BinaREST.GetOrderBook(symbol, 100);
            // Populate our depth cache
            depthResults.Asks.ForEach(a =>
            {
                if (a.Quantity != 0.00000000M)
                    marketDepthCache.Asks.Add(a.Price, a.Quantity);                
            });
            depthResults.Bids.ForEach(a =>
            {
                if (a.Quantity != 0.00000000M)
                    marketDepthCache.Bids.Add(a.Price, a.Quantity);
            });

            UpdateIDs[symbol] = depthResults.LastUpdateId;
            OrderBooks[symbol] = localDepthCache[symbol];
        }
        

        public decimal GetSpreadMiddle(string symbol)
        {
            try
            {
                var topBid = OrderBooks[symbol].Bids.Keys.Max();
                var topAsk = OrderBooks[symbol].Asks.Keys.Min();
                return (topAsk + topBid) / 2M;
            }
            catch(Exception ex)
            {
                lock (OrderBooks)
                {
                    var topBid = OrderBooks[symbol].Bids.Keys.Max();
                    var topAsk = OrderBooks[symbol].Asks.Keys.Min();
                    return (topAsk + topBid) / 2M;                    
                }  
            }                                         
        }


        private void PrintOrderBook(string symbol, int n)
        {
            while (true)
            {
                var bidsTop = OrderBooks[symbol].Bids.OrderByDescending(k => k.Key).Take(n).ToList();
                var asksTop = OrderBooks[symbol].Asks.OrderBy(k => k.Key).Take(n).ToList();

                Console.Clear();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("\r\n                #BIDS(Top" + n + ")    #ASKS(Top" + n + ")");
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("      ----------------------    ----------------------");
                for (int i = 0; i < n; i++)
                    Console.WriteLine("{0,16:0.0}  |  {1:0.00000}    {2:0.00000}  |  {3:0.0} ", bidsTop[i].Value, bidsTop[i].Key, asksTop[i].Key, asksTop[i].Value);
                Thread.Sleep(1000);
            }
        }
    }
}
