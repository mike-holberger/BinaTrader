using BinanceExchange.API.Client;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;

namespace BinaTrader
{
    class Program
    {
        public static Strategy_Phase1 strategy;

        public static async Task Main(string[] args)
        {   

            Console.WriteLine("--------------------------");
            Console.WriteLine("        BinaTrader");
            Console.WriteLine("--------------------------");

            // Provide your configuration and keys here, this allows the client to function as expected.
            string apiKey = ConfigurationManager.AppSettings["API_KEY"];
            string secretKey = ConfigurationManager.AppSettings["SECRET_KEY"];

            // Initialize logging:
            var Log = LogManager.GetLogger(typeof(Program));           
            Log.Debug("\n\nRUN START - " + DateTime.Now);

            // Initialise the general client client with config
            var client = new BinanceClient(new ClientConfiguration()
            {
                ApiKey = apiKey,
                SecretKey = secretKey,
                Logger = Log,
            });
            BinaREST.SetClient(client);

            // Initialze strategy markets, then execute strategy:
            strategy = new Strategy_Phase1();
            await strategy.Initialize();
            PrintOrderBook(Strategy_Phase1.SETTINGS.SubSpecificSymbols[0], 35);
            await strategy.Execute();       


            Console.WriteLine("\n\n\n\n\t*** END OF PROGRAM ***\n\n\t  Press enter 3 times to exit.");
            Console.ReadKey();
            Console.ReadKey();
            Console.ReadKey();

        }




        private static void PrintOrderBook(string symbol, int n)
        {
            var OutputThread = new Thread(() =>
            {
                while (true)
                {
                    List<KeyValuePair<decimal, decimal>> bidsTop = new List<KeyValuePair<decimal, decimal>>();
                    List<KeyValuePair<decimal, decimal>> asksTop = new List<KeyValuePair<decimal, decimal>>();

                    try
                    {
                        lock (strategy.depthDATA.OrderBooks)
                        {
                            bidsTop = strategy.depthDATA.OrderBooks[symbol].Bids.OrderByDescending(k => k.Key).Take(n).ToList();
                            asksTop = strategy.depthDATA.OrderBooks[symbol].Asks.OrderBy(k => k.Key).Take(n).ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        continue;
                    }
                        
                    
                    
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("\r\n                #BIDS(Top" + n + ")    #ASKS(Top" + n + ")");
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine("      ----------------------    ----------------------");

                    for (int i = 0; i < n; i++)
                    {
                        var bidPrice = bidsTop[i].Key;
                        var askPrice = asksTop[i].Key;

                        // Color code for order entered/StrategyStatus:
                        var P1buyOrders = strategy.OpenBUYorders.Where(o => o.Price == bidPrice || o.Price == askPrice);
                        var P1sellOrders = strategy.OpenSELLorders.Where(o => o.Price == bidPrice || o.Price == askPrice);
                        var P1Bid = P1buyOrders.FirstOrDefault(o => o.Price == bidPrice);
                        var P1BidTP = P1sellOrders.FirstOrDefault(o => o.Price == bidPrice);
                        var P1Ask = P1sellOrders.FirstOrDefault(o => o.Price == askPrice);
                        var P1AskTP = P1buyOrders.FirstOrDefault(o => o.Price == askPrice);
                        
                        var P2buyOrders = strategy.StrategyP2.P2_BuyOrders.Where(o => o.Price == bidPrice || o.Price == askPrice);
                        var P2sellOrders = strategy.StrategyP2.P2_SellOrders.Where(o => o.Price == bidPrice || o.Price == askPrice);
                        var P2Bid = P2buyOrders.FirstOrDefault(o => o.Price == bidPrice);
                        var P2BidTP = P2sellOrders.FirstOrDefault(o => o.Price == bidPrice);
                        var P2Ask = P2sellOrders.FirstOrDefault(o => o.Price == askPrice);
                        var P2AskTP = P2buyOrders.FirstOrDefault(o => o.Price == askPrice);

                        if (!P1buyOrders.Any() && !P1sellOrders.Any()
                            && !P2buyOrders.Any() && !P2sellOrders.Any())
                        {
                            Console.WriteLine("{0,16:0.0}  |  {1:0.00000}    {2:0.00000}  |  {3:0.0} ", bidsTop[i].Value, bidPrice, askPrice, asksTop[i].Value);
                        }
                        else
                        {
                            Console.Write("{0,16:0.0}  |  ", bidsTop[i].Value);
                            
                            if (P1buyOrders.Any(o => o.Price == bidPrice) && P1sellOrders.Any(o => o.Price == bidPrice))
                                Console.ForegroundColor = ConsoleColor.Magenta;                                                         
                            else if (P1Bid != null)
                            {
                                switch (P1Bid.StratState)
                                {
                                    case StrategyState.OPEN_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                                        break;
                                    case StrategyState.OPEN:
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        break;
                                    case StrategyState.OPEN_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.Yellow;
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                }
                            }
                            else if (P1BidTP != null)
                            {
                                switch (P1BidTP.StratState)
                                {
                                    case StrategyState.TP_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                                        break;
                                    case StrategyState.TP_ENTERED:
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                    case StrategyState.TP_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Blue;
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                }
                            }
                            else if (P2Bid != null)
                            {
                                switch (P2Bid.StratState)
                                {
                                    case StrategyState.OPEN_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkBlue;
                                        break;
                                    case StrategyState.OPEN:
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                    case StrategyState.OPEN_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.White;
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                }
                            }
                            else if (P2BidTP != null)
                            {
                                switch (P2BidTP.StratState)
                                {
                                    case StrategyState.TP_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkGray;
                                        break;
                                    case StrategyState.TP_ENTERED:
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                    case StrategyState.TP_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.White;
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                }
                            }


                            Console.Write("{0:0.00000}", bidPrice);
                            Console.ResetColor();
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.Write("    ");

                            if (P1buyOrders.Any(o => o.Price == askPrice) && P1sellOrders.Any(o => o.Price == askPrice))
                            {
                               Console.ForegroundColor = ConsoleColor.Magenta;
                            }
                            else if (P1Ask != null)
                            {
                                switch (P1Ask.StratState)
                                {
                                    case StrategyState.OPEN_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                                        break;
                                    case StrategyState.OPEN:
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        break;
                                    case StrategyState.OPEN_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.Yellow;
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                }
                            }
                            else if (P1AskTP != null)
                            {
                                switch (P1AskTP.StratState)
                                {
                                    case StrategyState.TP_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                                        break;
                                    case StrategyState.TP_ENTERED:
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                    case StrategyState.TP_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Blue;
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        break;
                                }
                            }
                            else if (P2Ask != null)
                            {
                                switch (P2Ask.StratState)
                                {
                                    case StrategyState.OPEN_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkBlue;
                                        break;
                                    case StrategyState.OPEN:
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                    case StrategyState.OPEN_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.White;
                                        Console.ForegroundColor = ConsoleColor.Cyan;
                                        break;
                                }
                            }
                            else if (P2AskTP != null)
                            {
                                switch (P2AskTP.StratState)
                                {
                                    case StrategyState.TP_UNCONFIRMED:
                                        Console.ForegroundColor = ConsoleColor.DarkGray;
                                        break;
                                    case StrategyState.TP_ENTERED:
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                    case StrategyState.TP_FILLED:
                                        Console.BackgroundColor = ConsoleColor.Green;
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                    case StrategyState.PARTIAL_FILL:
                                        Console.BackgroundColor = ConsoleColor.White;
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        break;
                                }
                            }

                            Console.Write("{0:0.00000}", askPrice);
                            Console.ResetColor();
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine("  |  {0:0.0} ", asksTop[i].Value);
                        }
                    }

                    Console.WriteLine();
                    for (int i = 0; i < strategy.OpenBUYorders.Count; i++)
                    {
                        if (strategy.OpenBUYorders.ElementAtOrDefault(i) != null)
                            Console.Write($"{strategy.OpenBUYorders[i].StratState,27} :::: ");
                        else
                            Console.Write($"{"NONE",27} :::: ");

                        if (strategy.OpenSELLorders.ElementAtOrDefault(i) != null)
                            Console.WriteLine($"{strategy.OpenSELLorders[i].StratState}");
                        else
                            Console.WriteLine("NONE");
                    }
                        
                    Console.WriteLine($"{strategy.BUYProfitCycles + strategy.StrategyP2.BUYProfitCyclesP2, 27} :::: {strategy.SELLProfitCycles + strategy.StrategyP2.SELLProfitCyclesP2}");
                    // DELAY
                    Thread.Sleep(850);
                }
            });

            OutputThread.IsBackground = true;
            OutputThread.Name = "Console-Output-Thread";
            OutputThread.Start();
        }

        
    }
}