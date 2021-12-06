using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace EternalDurableFunction
{
    public static class EternalDurableFunction
    {
        // these are a few parameters that would define the functions names. Set the FunctionId one to whatever your process is, and let the rest defie their own names
        internal const string FunctionId = "funcid";

        internal const string FunctionOrchestratorName = $"{FunctionId}_orchestrator";
        internal const string FunctionHttpStartName = $"{FunctionId}_httpstart";
        internal const string FunctionExecutorName = $"{FunctionId}_executor";

        internal const int MinutesToWaitAfterNoRecordsProcessed = 1;
        internal const int MinutesToWaitAfterErrorInRecordsProcessing = 3;


        //HttpStart
        [FunctionName(FunctionHttpStartName)]
        public static async Task<HttpResponseMessage> HttpStart([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req, [DurableClient] IDurableOrchestrationClient starter, ILogger log)
        {
            var existingInstance = await starter.GetStatusAsync(FunctionId);
            if (
                existingInstance == null
            || existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Completed
            || existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Failed
            || existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Terminated)
            {
                string instanceId = await starter.StartNewAsync(FunctionOrchestratorName, FunctionId);

                log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

                return starter.CreateCheckStatusResponse(req, FunctionId);
            }
            else
            {
                // An instance with the specified ID exists or an existing one still running, don't create one.
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent($"An instance with ID '{FunctionId}' already exists."),
                };
            }
        }


        //Orchestrator:
        [FunctionName(FunctionOrchestratorName)]
        public static async Task RunOrchestrator([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger logger)
        {
            logger = context.CreateReplaySafeLogger(logger);

            int postpone_in_minutes = 0;

            try
            {
                var results = await context.CallActivityAsync<int>(FunctionExecutorName, null);

                if (results > 0)
                {
                    logger.LogWarning($"{results} records were succesfuly processed.");
                }
                else
                {
                    logger.LogWarning($"No records were processed. Pausing for {MinutesToWaitAfterNoRecordsProcessed} min(s).");

                    postpone_in_minutes = MinutesToWaitAfterNoRecordsProcessed;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"{ex.Message} [{ex.InnerException?.Message}]");
                logger.LogWarning($"An error ocurred. Pausing for {MinutesToWaitAfterErrorInRecordsProcessing} min(s).");

                postpone_in_minutes = MinutesToWaitAfterErrorInRecordsProcessing;
            }

            if (postpone_in_minutes > 0)
                await context.CreateTimer(context.CurrentUtcDateTime.Add(TimeSpan.FromMinutes(postpone_in_minutes)), CancellationToken.None);

            context.ContinueAsNew(null);
        }

        //ActivityFunction
        [FunctionName(FunctionExecutorName)]
        public static async Task<int> ActivityFunction([ActivityTrigger] object input, ILogger logger)
        {

            return await ProcessRecordsSimulation(logger);

        }
        
       


        //Processor
        public static async Task<int> ProcessRecordsSimulation(ILogger logger)
        {

            var results = new Random().Next(0, 5);

            logger.LogInformation(results > 0 ? "It simulates that 1 or more records were processed." : "It sumlates that 0 records were processed.");


            return results;

        }
    }
}