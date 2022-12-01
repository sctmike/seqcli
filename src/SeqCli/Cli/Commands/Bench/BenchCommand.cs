// Copyright Datalust Pty Ltd and Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Seq.Api.Model.Signals;
using SeqCli.Cli.Features;
using SeqCli.Connection;
using SeqCli.Util;
using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace SeqCli.Cli.Commands.Bench;

/*
 * Run performance benchmark tests against a Seq server.
 *
 * Requires test cases in a JSON file matching the format of `BenchCases.json`.
 * 
 * If a Seq reporting server is configured this command logs data such as:
 * 
 * {
    "@t": "2022-11-09T01:12:06.0293545Z",
    "@mt": "Bench run {BenchRunId} for query {Id}. Mean {MeanElapsed:N0} ms with relative dispersion {RelativeStandardDeviationElapsed:N2}",
    "@m": "Bench run \"2907\" for query \"with-signal\". Mean 4 ms with relative dispersion 0.06",
    "@i": "bb0c84a5",
    "@r": [
        "4",
        "0.06"
    ],
    "BenchRunId": "2907",
    "End": "2022-08-15T00:00:00.0000000",
    "Id": "with-signal",
    "LastResult": 606,
    "MaxElapsed": 4.082,
    "MeanElapsed": 3.6676,
    "MinElapsed": 3.4334,
    "Query": "select count(*) from stream where @Level = 'Warning'",
    "RelativeStandardDeviationElapsed": 0.05619408341421253,
    "Runs": 10,
    "SignalExpression": "signal-m33302",
    "Start": "2022-08-14T16:00:00.0000000"
}
 */
[Command("bench", @"Measure query performance")]
class BenchCommand : Command
{
    readonly SeqConnectionFactory _connectionFactory;
    int _runs = 3;
    readonly ConnectionFeature _connection;
    readonly DateRangeFeature _range;
    string _cases = "";
    string _reportingServerUrl = "";
    string _reportingServerApiKey = "";
    string _description = "";
    
    public BenchCommand(SeqConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        Options.Add("r|runs=", "The number of runs to execute", r =>
        {
            int.TryParse(r, out _runs);
        });
        
        Options.Add(
            "c|cases=", 
            @"A JSON file containing the set of cases to run. Defaults to a standard set of cases.", 
            c => _cases = c);

        _connection = Enable<ConnectionFeature>();
        _range = Enable<DateRangeFeature>();
        
        Options.Add(
            "reporting-server=", 
            "The address of a Seq server to send bench results to", 
            s => _reportingServerUrl = s);
        Options.Add(
            "reporting-apikey=", 
            "The API key to use when connecting to the reporting server", 
            a => _reportingServerApiKey = a);
        Options.Add(
            "description=", 
            "Optional description of the bench test run", 
            a => _description = a);
    }
    
    protected override async Task<int> Run()
    {
        try
        {
            var connection = _connectionFactory.Connect(_connection);
            using var reportingLogger = BuildReportingLogger();
            var cases = ReadCases(_cases);
            var runId = Guid.NewGuid().ToString("N").Substring(0, 4);

            if (_range.Start == null || _range.End == null)
            {
                Log.Error("Both the `start` and `end` arguments are required");
                return 1;
            }

            foreach (var c in cases.Cases)
            {
                var timings = new BenchCaseTimings();
                object? lastResult = null;
                
                foreach (var i in Enumerable.Range(1, _runs))
                {
                    var response = await connection.Data.QueryAsync(
                        c.Query,
                        _range.Start,
                        _range.End,
                        SignalExpressionPart.Signal(c.SignalExpression)
                    );

                    timings.PushElapsed(response.Statistics.ElapsedMilliseconds);

                    if (response.Rows != null)
                    {
                        var isScalarResult = response.Rows.Length == 1 && response.Rows[0].Length == 1;
                        if (isScalarResult && i == _runs)
                        {
                            lastResult = response.Rows[0][0];
                        }
                    }
                }
                
                using (lastResult != null ? LogContext.PushProperty("LastResult", lastResult) : null)
                using (LogContext.PushProperty("MinElapsed", timings.MinElapsed))
                using (LogContext.PushProperty("MaxElapsed", timings.MaxElapsed))
                using (LogContext.PushProperty("Runs", _runs))
                using (!string.IsNullOrWhiteSpace(c.SignalExpression) ? LogContext.PushProperty("SignalExpression", c.SignalExpression) : null)
                using (LogContext.PushProperty("Start", _range.Start))
                using (LogContext.PushProperty("StandardDeviationElapsed", timings.StandardDeviationElapsed))
                using (LogContext.PushProperty("End", _range.End))
                using (LogContext.PushProperty("Query", c.Query))
                using (!string.IsNullOrWhiteSpace(_description) ? LogContext.PushProperty("Description", _description) : null)
                {
                    reportingLogger.Information(
                        "Bench run {Cases}/{RunId} against {Server} for query {Id}: mean {MeanElapsed:N0} ms with relative dispersion {RelativeStandardDeviationElapsed:N2}", 
                                 cases.CasesHash, runId,  _connection.Url,     c.Id,      timings.MeanElapsed,                      timings.RelativeStandardDeviationElapsed);
                }
            }

            return 0;
        } 
        catch (Exception ex)
        {
            Log.Error(ex, "Benchmarking failed: {ErrorMessage}", ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Build a second Serilog logger for logging benchmark results. 
    /// </summary>
    Logger BuildReportingLogger()
    { 
        var loggerConfiguration = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(_reportingServerUrl))
            loggerConfiguration.WriteTo.Seq(
                _reportingServerUrl, 
                apiKey: string.IsNullOrWhiteSpace(_reportingServerApiKey) ? null : _reportingServerApiKey,
                period: TimeSpan.FromMilliseconds(1));

        return loggerConfiguration.CreateLogger();
    }

    /// <summary>
    /// Read and parse the bench test cases from the file supplied or else from a default file. 
    /// </summary>
    static BenchCasesCollection ReadCases(string filename)
    {
        var defaultCasesPath = Content.GetPath("Cli/Commands/Bench/BenchCases.json");
        var casesString = File.ReadAllText(string.IsNullOrWhiteSpace(filename)
            ? defaultCasesPath
            : filename);
        var casesFile = JsonConvert.DeserializeObject<BenchCasesCollection>(casesString)
                        ?? new BenchCasesCollection();

        casesFile.CasesHash = HashString(casesString); 

        if (casesFile.Cases.Select(c => c.Id).Distinct().Count() != casesFile.Cases.Count)
        {
            throw new Exception($"Cases file {filename} contains a duplicate id");
        }

        if (!casesFile.Cases.Any())
        {
            throw new Exception($"Cases file {filename} contains no cases");
        }

        return casesFile;
    }

    static string HashString(string input)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.ASCII.GetBytes(input);
        var hash = Convert.ToHexString(md5.ComputeHash(bytes));
        return new string(new [] {hash[4], hash[8], hash[16], hash[24]});
    }
}
