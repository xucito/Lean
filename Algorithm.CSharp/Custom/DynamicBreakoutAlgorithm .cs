using System;
using System.Linq;
using QuantConnect.Indicators;
using MathNet.Numerics.Statistics;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using System.Collections.Generic;
using QuantConnect.Data.Market;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Orders;
using QuantConnect.Algorithm.CSharp.Custom;

namespace QuantConnect
{
    // https://www.quantconnect.com/tutorials/dynamic-breakout-ii-strategy/
    public class DynamicBreakoutAlgorithm : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2016, 10, 1);
            SetEndDate(2020, 12, 1);
            SetCash(100000);

            var symbols = SymbolList.Symbols;

            UniverseSettings.Resolution = Resolution.Daily;
            SetUniverseSelection(new ManualUniverseSelectionModel(symbols));
            SetAlpha(new MOMAlphaModel());
            SetPortfolioConstruction(new EqualWeightingPortfolioConstructionModel());
            SetRiskManagement(new MaximumDrawdownPercentPerSecurity(0.02m));

            AddUniverse(coarse =>
            {
                // Properties available on the CoarseFundamental type 'stock'
                // stock.DollarVollume
                // stock.Value (daily close)
                // stock.Volume
                // stock.Market
                //
                return (from c in coarse
                        orderby c.Value descending
                        select c.Symbol).Take(500);
            });
            SetBenchmark("SPY");

            //1. Set the Execution handler to a new instance of ImmediateExecutionModel()
            SetExecution(new MyExecutionModel());
        }
    }

    // Basic Execution Model Scaffolding Structure Example
    public class MyExecutionModel : ExecutionModel
    {
        private readonly PortfolioTargetCollection _targetsCollection = new PortfolioTargetCollection();
        private Dictionary<Symbol, int> _cooldownPeriod = new Dictionary<Symbol, int>();

        // Fill the supplied portfolio targets efficiently.
        public override void Execute(QCAlgorithm algorithm, IPortfolioTarget[] targets)
        {
            foreach (var sec in algorithm.Portfolio.Securities)
            {
                if (sec.Value.Symbol.SecurityType == SecurityType.Equity && sec.Value.Holdings.Quantity > 0)
                {
                    if (((sec.Value.Holdings.HoldingsValue - sec.Value.Holdings.AbsoluteHoldingsCost) / sec.Value.Holdings.AbsoluteHoldingsCost < -0.1m))
                    {
                        Console.WriteLine("Triggered stop-loss for " + sec.Key.ID.Symbol);
                        algorithm.MarketOrder(sec.Key, -1 * sec.Value.Holdings.Quantity);
                        if (!_cooldownPeriod.ContainsKey(sec.Value.Symbol))
                            _cooldownPeriod.Add(sec.Value.Symbol, 14);
                    }
                    else if (!((MOMAlphaModel)algorithm.Alpha).topMomentum.Contains(sec.Key))
                    {
                        Console.WriteLine("Triggered sell because momentum is negative " + sec.Key.ID.Symbol);
                        algorithm.MarketOrder(sec.Key, -1 * sec.Value.Holdings.Quantity);
                        if (!_cooldownPeriod.ContainsKey(sec.Value.Symbol))
                            _cooldownPeriod.Add(sec.Value.Symbol, 14);
                    }
                    else if ((sec.Value.Holdings.HoldingsValue - sec.Value.Holdings.AbsoluteHoldingsCost) / sec.Value.Holdings.AbsoluteHoldingsCost >= .35m)
                    {
                        Console.WriteLine("Triggered sell for " + sec.Key.ID.Symbol);
                        algorithm.MarketOrder(sec.Key, -1 * sec.Value.Holdings.Quantity);
                        if (!_cooldownPeriod.ContainsKey(sec.Value.Symbol))
                            _cooldownPeriod.Add(sec.Value.Symbol, 14);
                    }
                }
            }

            int additionalToHold = 0;
            int totalToHold = CalculateNumberOfAvailableAssetsToHold(algorithm);
            int currentHoldings = algorithm.Portfolio.Securities.Where(sec => sec.Value.Symbol.SecurityType == SecurityType.Equity && sec.Value.Holdings.Quantity > 0).Count();


            _targetsCollection.AddRange(targets);
            // for performance we check count value, OrderByMarginImpact and ClearFulfilled are expensive to call
            if (_targetsCollection.Count > 0)
            {
                foreach (var target in _targetsCollection.OrderByMarginImpact(algorithm))
                {
                    // calculate remaining quantity to be ordered
                    var quantity = CalculateBalancedQuantity(algorithm, target.Symbol, OrderSizing.GetUnorderedQuantity(algorithm, target));
                    if (quantity > 0 && !_cooldownPeriod.ContainsKey(target.Symbol) && (currentHoldings + additionalToHold) < totalToHold)
                    {
                        Console.WriteLine("Buying " + quantity + " " + target.Symbol);
                        algorithm.MarketOrder(target.Symbol, quantity);
                        additionalToHold++;
                    }
                }

                _targetsCollection.ClearFulfilled(algorithm);
            }


            for (int index = 0; index < _cooldownPeriod.Count; index++)
            {
                var item = _cooldownPeriod.ElementAt(index);
                _cooldownPeriod[_cooldownPeriod.ElementAt(index).Key] = _cooldownPeriod.ElementAt(index).Value - 1;
            }

            _cooldownPeriod = _cooldownPeriod.Where(cp => cp.Value > 0).ToDictionary(x => x.Key, x => x.Value);

        }

        private int CalculateNumberOfAvailableAssetsToHold(QCAlgorithm algorithm)
        {
            var totalValue = algorithm.Portfolio.TotalPortfolioValue;

            if (totalValue < 100000)
            {
                return 2;
            }
            else if (totalValue < 200000)
            {
                return 3;
            }
            else if (totalValue < 250000)
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        private decimal CalculateBalancedQuantity(QCAlgorithm algorithm, Symbol symbol, decimal quantity)
        {
            var totalValue = algorithm.Portfolio.TotalPortfolioValue;
            if (algorithm.ActiveSecurities.ContainsKey(symbol))
            {
                var totalTransactionValue = (quantity * algorithm.ActiveSecurities[symbol].Open) - algorithm.Portfolio[symbol].HoldingsValue;

                var totalNumberOfAssets = CalculateNumberOfAvailableAssetsToHold(algorithm);

                var valueToBuy = totalTransactionValue < (totalValue / totalNumberOfAssets) ? totalTransactionValue : (totalValue / totalNumberOfAssets);

                valueToBuy = valueToBuy < algorithm.Portfolio.Cash ? valueToBuy : algorithm.Portfolio.Cash;

                return Math.Floor((valueToBuy / algorithm.ActiveSecurities[symbol].Open));
            }
            return 0;
        }

        //  Optional: Securities changes event for handling new securities.
        public override void OnSecuritiesChanged(QCAlgorithm algorithm,
                                                 SecurityChanges changes)
        {

        }
    }

    public class MOMAlphaModel : AlphaModel
    {
        public Dictionary<Symbol, ExponentialMovingAverage> mom = new Dictionary<Symbol, ExponentialMovingAverage>();
        public List<Symbol> topMomentum = new List<Symbol>();

        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var symbol = security.Symbol;
                if (!mom.ContainsKey(symbol))
                    mom.Add(symbol, algorithm.EMA(symbol, 14, Resolution.Daily));
            }
        }

        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            var ordered = mom.OrderByDescending(kvp => kvp.Value);
            topMomentum = ordered.Take(100).Select(o => o.Key).ToList();
            var filtered = ordered.Take(50).Where(o => !algorithm.Portfolio[o.Key].Invested).Take(10);

            return Insight.Group(
                filtered.Select(o => Insight.Price(o.Key, TimeSpan.FromDays(1), InsightDirection.Up, (double)o.Value.Current.Value)).ToArray()
            );
        }
    }
}