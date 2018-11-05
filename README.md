# BinaTrader
This program uses a 2-phase "ladder" style trading strategy to profit from sideways-trending market volitility oscilations (on Binance Exchange).

Language: C#

Framework Target: .NET 4.7+

In Phase 1: (5) orders are  on either side of the orderbook spread middle (at 0.5% price intervals) that move to follow the spread, then place stationary take profits (+/-0.75%) on execution. Then when all these spread following orders, after several profit cycles, become stationary take profits on the high and low end of price movement, and the price drifts in between, inside this window. Then Phase 2 is initiated.

Phase 2 grids out stationary orders (at 0.5% intervals) on both sides, inside the created price window. Then as the price oscillates inside the window, these orders are executed and corresponding take-profit orders are placed at (+/-0.75%)
then if the price moves to the boundaries of the window, it executes the phase 1 take profits, cancels the phase2 open orders, (leaving phase2 take profit orders open). This initiates phase 1 and adjusts the window boundaries using the original (phase 1) moving, spread following orders.


API Keys are entered inside the App.Config file. Only one key-pair is required. 

Parameters such as price intervals (percentages) and wager amount can be adjusted in the Strategy_Phase1 class file: SETTINGS internal class.
