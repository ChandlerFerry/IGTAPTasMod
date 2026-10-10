using System;
using System.Collections.Generic;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    static class RunSelfTest
    {
        static int _passed;

        static void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + what);
            _passed++;
        }

        public static int Run(string[] args)
        {
            try
            {
                EconomyGoals();
                Console.WriteLine($"run-selftest: {_passed} checks passed");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }
        }

        static void EconomyGoals()
        {
            foreach (string token in new[] { "buyatom", "buyclonecd8", "buyclonegp8", "buycd8", "buycddigits8", "buyatomlevel9", "buyatomlevel10",
                "buywatts10", "buynp10", "buyfree6b", "buyfree10a", "buystatue", "buytree0", "buyclone6" })
                Check(FullTasGoal.IsEconomy("buy:" + token), "buy:" + token + " is an economy goal");
            foreach (string label in new[] { "buy:buytree5", "buy:buytree16", "buy:buywj", "buy:buyteleporters", "at:16639,11761,240,400", "reward:bonus3" })
                Check(!FullTasGoal.IsEconomy(label), label + " stays in the run's skeleton");
            Check(FullTasGoal.IsEconomy("farm:6") && FullTasGoal.IsEconomy("record:7"), "laps are economy goals");
        }
    }
}
