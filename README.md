<h1 align="center">
  <a href="https://github.com/hgjura/example-of-eternal-durable-azure-functions">
    <img src=".github/logo.png" alt="Logo" width="650" height="150">
  </a>
</h1>

<div align="center">
  <h1>Eternal durable Azure functions</h1>
  
  <h2>An example on how to create a durable functin that runs continuosly.</h2>
  <!-- <a href="#about"><strong>Explore the screenshots »</strong></a>
  <br />
  <br /> -->
  <a href="https://github.com/hgjura/example-of-eternal-durable-azure-functions/new?assignees=&labels=&template=01_bug_report.yml&title=%5BBUG%5D">Report a Bug</a>
  ·
  <a href="https://github.com/hgjura/example-of-eternal-durable-azure-functions/new?assignees=&labels=&template=02_feature_request.yml&title=%5BFEATURE+REQ%5D">Request a Feature</a>
  .
  <a href="https://github.com/hgjura/example-of-eternal-durable-azure-functions/new?assignees=&labels=&template=03_question.yml&title=%5BQUERY%5D">Ask a Question</a>
</div>

<div align="center">
<br />

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT) [![PRs welcome](https://img.shields.io/badge/PRs-welcome-ff69b4.svg?style=flat-square)](https://github.com/hgjura/command-pattern-with-queues/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22) [![Gitter](https://badges.gitter.im/hgjura/ServerCommands.svg)](https://gitter.im/hgjura/ServerCommands?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge)

[![code with hearth by Herald Gjura](https://img.shields.io/badge/%3C%2F%3E%20with%20%E2%99%A5%20by-hgjura-ff1414.svg?style=flat-square)](https://github.com/hgjura)

</div>



---

## About

This is an example of creating eternal durable functions, or durable functions that run continuously.

What are durable functions:
> Durable Functions is an extension of Azure Functions that lets you write stateful functions in a serverless compute environment. The extension lets you define stateful workflows by writing orchestrator functions and stateful entities by writing entity functions using the Azure Functions programming model. Behind the scenes, the extension manages state, checkpoints, and restarts for you, allowing you to focus on your business logic. [From Microsoft documentation](https://docs.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-overview?tabs=csharp).

In short Durable Functions are Azure Functions that run, asynchronously, other Functions through an Orchestrator and able as such to create a workflow.

What is an Eternal Orchestrator or Function:
> Eternal orchestrations are orchestrator functions that never end. They are useful when you want to use Durable Functions for aggregators and any scenario that requires an infinite loop. [From Microsoft documentation](https://docs.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-eternal-orchestrations?tabs=csharp).

Let's see how all comes together!



### Built With
- C# (NET 5.0)
- Azure Functions

# Getting Started

This project, that illustrates one such eternal function, is made of four parts:

> Prior to jumping into the code note a trick I use often to name some of these Azure functions. The solution is made of three functions. However, I set a ```const``` property ```FunctionId``` at the top of the file and define dynamically all the names of the functiosn as to keep everything consistent, and not make errors by copying-and-pasting.

To simplify the example here I am choosing to have only one function run continuously. I am simulating a method that inserts or updates records in a datastore, or updates blobs in a storage, in a batch. And when that batch is completed starts again, and again, until there are no more records to update.

#### HttpStart Function
This is the functions that sit in the top of the hierarchy. The role of this is to kickstart initially the orchestrator and make sure there is no interference with the run.

Here I check if any function with the same ```FunctionId``` is registered or running. And if yes, do not start another one, or if not, start a new orchestrator.

This guarantees that only one function of the same id is running at any given time.
```cs
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

```






#### Orchestrator Function
This is a function that is started by the HttpStart, and as the name implies, orchestrates all the other functions. For simplicity, I am choosing for the orchestrator to only run one activity functions, that maybe does a few things. 

> **Important**. Make sure to call the ```context.CreateReplaySafeLogger(logger)``` to get back a thread-safe logger, otherwise you will get some strange logging calls that will give the illusion that the activity function is running in random times. It is not, it is just the logger that 

This activity function returns an ```int``` which is the number of records that is inserted, updated, or in short, processes by the activity function.

Now, at this point the orchestrator, in conjunction with the activity function has some decisions to make:
- Decision 1. What to do when activity function returns a number that is > 0, meaning that it has processed 1 or more records? This is easy. Start over again.
- Decision 2. What to do when activity function returns 0, meaning that it ran and found no records to process. At this time, if you start the activity functions again, you will get the same result, and will create a fast-running loop that does nothing. So, it is wise to snooze the functions for a short period of time. I have created a ```const```, named ```MinutesToWaitAfterNoRecordsProcessed``` to hold the number of minutes we want to postpone the next execution.
- Decision 3: What to do when activity function runs, and for whatever reason it throws a non-transient error, meaning that something is irrecoverably wrong with the processor. In that case, we don't want to start the processor again, but rather log the error, notify someone (if necessary) and postpone the next execution by a certain number of minutes. I have created a ```const```, named ```MinutesToWaitAfterErrorInRecordsProcessing``` that holds the number of minutes that we will postpone the function due to the exception.

```cs

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
```



#### Activity Function
Next in the call chain is the Activity Function. Right now, because of simplicity, this function calls a method that simulates a call to a processor that does some work and return the number of records processed.

```cs
public static async Task<int> ActivityFunction([ActivityTrigger] object input, ILogger logger)
{
    return await ProcessRecordsSimulation(logger);
}
```

#### Processor 

A method ```ProcessRecordsSimulation``` that simulates a processing or batch unit that is called by the activity function.
```cs
public static async Task<int> ProcessRecordsSimulation(ILogger logger)
{
    var results = new Random().Next(0, 5);
    logger.LogInformation(results > 0 
        ? "It simulates that 1 or more records were processed." 
        : "It sumlates that 0 records were processed.");
    return results;
}
```



As you will see in the code of the solution, with a few minor tweaks, you can abstract this solution enough to use it generically in any situation when you need some processing to happen in a continuous fashion.

Enjoy!





























## Support

<!-- > **[?]**
> Provide additional ways to contact the project maintainer/maintainers. -->

Reach out to the maintainer at one of the following places:

- [GitHub issues](https://github.com/hgjura/example-of-eternal-durable-azure-functions/new?assignees=&labels=&template=01_bug_report.yml&title=%5BSUPPORT%5D)
- The email which is located [in GitHub profile](https://github.com/hgjura)

## Project assistance

If you want to say **thank you** or/and support active development of ServerTools.ServerCommands:

- Add a [GitHub Star](https://github.com/hgjura/example-of-eternal-durable-azure-functions) to the project.
- Tweet about the ServerTools.ServerCommands on your Twitter.
- Write interesting articles about the project on [Dev.to](https://dev.to/), [Medium](https://medium.com/) or personal blog.

Together, we can make ServerTools.ServerCommands **better**!

## Contributing

First off, thanks for taking the time to contribute! Contributions are what make the open-source community such an amazing place to learn, inspire, and create. Any contributions you make will benefit everybody else and are **greatly appreciated**.

We have set up a separate document containing our [contribution guidelines](.github/CONTRIBUTING.md).

Thank you for being involved!

## Authors & contributors

The original setup of this repository is by [Herald Gjura](https://github.com/hgjura).

For a full list of all authors and contributors, check [the contributor's page](https://github.com/hgjura/example-of-eternal-durable-azure-functions/contributors).

## Security

ServerTools.ServerCommands follows good practices of security, but 100% security can't be granted in software.
ServerTools.ServerCommands is provided **"as is"** without any **warranty**. Use at your own risk.

_For more info, please refer to the [security](.github/SECURITY.md)._

## License

This project is licensed under the **MIT license**.

Copyright 2021 [Herald Gjura](https://github.com/hgjura)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
