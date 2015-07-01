#if INTERACTIVE
#r "bin/Release/FSharp.Actor.dll"
#r "bin/Release/NodaTime.dll"
#r "bin/Release/Logary.dll"
#r "bin/Release/Logary.Riemann.dll"
#endif

open System

open NodaTime

open Logary
open Logary.Logentries
open Logary.Configuration
open Logary.Targets
open Logary.Metrics
open System.Threading

[<EntryPoint>]
let main argv =
  use logary =
    withLogary' "Riemann.Example" (
      withTargets [
        //Riemann.create (Riemann.RiemannConf.Create(tags = ["riemann-health"])) "riemann"
        Console.create (Console.empty) "console"
        Logentries.create ({ Logentries.empty with useHttp = true; useTls = false; token = "bb853e97-8e3f-4d2d-9744-0f1b179ac61b" }) "logentries"
        //Logstash.create (Logstash.LogstashConf.Create("logstash.prod.corp.tld", 1939us)) "logstash"
      ] >>
      withMetrics (Duration.FromSeconds 4L) [
        WinPerfCounters.create (WinPerfCounters.Common.cpuTimeConf) "cpuTime" (Duration.FromMilliseconds 500L)
      ] >>
      withRules [
        Rule.createForTarget "console"
        Rule.createForTarget "logentries"
      ] >>
      withInternalTargets Info [
        Console.create (Console.empty) "console"
      ]
    )

  let logger = logary.GetLogger ("Testerdetest")

  while (true) do
    logger.Log (LogLine.debug "Testerdetest")
    Thread.Sleep 200

  Console.ReadKey true |> ignore
  0
