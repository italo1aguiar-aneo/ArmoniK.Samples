// This file is part of the ArmoniK project
//
// Copyright (C) ANEO, 2021-$CURRENT_YEAR$. All rights reserved.
//   W. Kirschenmann   <wkirschenmann@aneo.fr>
//   J. Gurhem         <jgurhem@aneo.fr>
//   D. Dubuc          <ddubuc@aneo.fr>
//   L. Ziane Khodja   <lzianekhodja@aneo.fr>
//   F. Lemaitre       <flemaitre@aneo.fr>
//   S. Djebbar        <sdjebbar@aneo.fr>
//   J. Fonseca        <jfonseca@aneo.fr>
//   D. Brasseur       <dbrasseur@aneo.fr>
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using ArmoniK.Api.Common.Channel.Utils;
using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.Worker.Worker;
using System.Text;

using ArmoniK.Api.gRPC.V1.Agent;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Google.Protobuf.Collections;

namespace ArmoniK.Samples.SubTasking.Worker
{
  public class SubTaskingWorker : WorkerStreamWrapper
  {
    /// <summary>
    ///   Initializes an instance of <see cref="SubTaskingWorker" />
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers</param>
    /// <param name="provider">gRPC channel provider to send tasks and results to ArmoniK Scheduler</param>
    public SubTaskingWorker(ILoggerFactory loggerFactory, GrpcChannelProvider provider)
      : base(loggerFactory,
             provider)
      => logger_ = loggerFactory.CreateLogger<SubTaskingWorker>();

    public override async Task<Output> Process(ITaskHandler taskHandler)
    {
      using var scopedLog = logger_.BeginNamedScope("Execute task",
                                              ("sessionId", taskHandler.SessionId),
                                              ("taskId", taskHandler.TaskId));
      try
      {
        // We may use TaskOptions.Options to send a field UseCase where we inform
        // what should be executed
        var useCase = taskHandler.TaskOptions.Options["UseCase"];

        switch (useCase)
        {
          case "Launch":
            await Launch(taskHandler); break;
          case "Joiner":
            await Joiner(taskHandler); break;
          case "HelloWorker":
            await HelloWorker(taskHandler); break;
          default:
            return new Output
            {
              Error = new Output.Types.Error
              {
                Details = "UseCase not found",
              },
            };
        }
      }
      catch (Exception e)
      {
        logger_.LogError(e,
                         "Error during task computing.");
        return new Output
        {
          Error = new Output.Types.Error
          {
            Details = e.Message,
          },
        };
      }
      return new Output
      {
        Ok = new Api.gRPC.V1.Empty(),
      };
    }
    // Launching helloWorkers and joiner, both are tasks created by our primary task
    public async Task Launch(ITaskHandler taskHandler)
    {
        logger_.Log(LogLevel.Debug, $"Launching workers");
        var resultIds = await SubmitWorkers(taskHandler);
        await SubmitJoiner(taskHandler, resultIds);
    }

    public async Task<List<string>> SubmitWorkers(ITaskHandler taskHandler)
    {
      logger_.Log(LogLevel.Debug, $"Submitting Workers");

      var input = Encoding.ASCII.GetString(taskHandler.Payload);

      var taskOptions = new TaskOptions
      {
        MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
        MaxRetries = 2,
        Priority = 1,
        PartitionId = taskHandler.TaskOptions.PartitionId,
        Options =
            {
              new MapField<string, string>
              {
                { "UseCase", "HelloWorker" }
              }
            }
      };

      var subTaskResults = await taskHandler.CreateResultsMetaDataAsync(
          Enumerable.Range(1, 5).Select(i =>
            new CreateResultsMetaDataRequest.Types.ResultCreate
            {
              Name = Guid.NewGuid() + "_" + i
            }).ToList()
            );

      var subTasksResultIds = subTaskResults.Results.Select(result => result.ResultId)
                                .ToList();

      CreateResultsResponse payload = await taskHandler.CreateResultsAsync(
         new List<CreateResultsRequest.Types.ResultCreate>
           {
               new CreateResultsRequest.Types.ResultCreate
              {
                Data = UnsafeByteOperations.UnsafeWrap(Encoding.ASCII.GetBytes($"{input}_FatherId_{taskHandler.TaskId}")),
                Name = "Payload",
              }
           }
      );

      var payloadId = payload.Results.Single().ResultId;

      var submitTasksResponse = await taskHandler.SubmitTasksAsync(new List<SubmitTasksRequest.Types.TaskCreation>
          (
           subTasksResultIds
            .Select(subTaskId => new SubmitTasksRequest.Types.TaskCreation
            {
              PayloadId = payloadId,
              ExpectedOutputKeys = { subTaskId }
            })
            .ToList()
          ),
        taskOptions
      );

      return subTasksResultIds;

    }

    public async Task SubmitJoiner(ITaskHandler taskHandler, List<string> expectedOutputIds)
    {
      logger_.Log(LogLevel.Debug, $"Submitting Joiner");
      var taskOptions = new TaskOptions
      {
        MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
        MaxRetries = 2,
        Priority = 1,
        PartitionId = taskHandler.TaskOptions.PartitionId,
        Options =
            {
              new MapField<string, string>
              {
                { "UseCase", "Joiner" }
              }
            }
      };

      var subTaskResultId = taskHandler.ExpectedResults.Single();

      CreateResultsResponse payload = await taskHandler.CreateResultsAsync(
        new List<CreateResultsRequest.Types.ResultCreate>
        {
               new CreateResultsRequest.Types.ResultCreate
              {
                Data = UnsafeByteOperations.UnsafeWrap(Encoding.ASCII.GetBytes($"Submiting Joiner")),
                Name = "Payload",
              }
        }
      );

      var payloadId = payload.Results.Single().ResultId;

      await taskHandler.SubmitTasksAsync(new List<SubmitTasksRequest.Types.TaskCreation>
          {
            new SubmitTasksRequest.Types.TaskCreation
            {
              PayloadId = payloadId,
              ExpectedOutputKeys =
              {
                subTaskResultId,
              },
              DataDependencies = { expectedOutputIds },
            }
          },
        taskOptions
      );
    }

    public async Task HelloWorker(ITaskHandler taskHandler)
    {
      var input = Encoding.ASCII.GetString(taskHandler.Payload);

      var resultId = taskHandler.ExpectedResults.Single();
      // We the result of the task using through the handler
      await taskHandler.SendResult(resultId,
                                   Encoding.ASCII.GetBytes($"{input}_SonId_{taskHandler.TaskId}"))
                       .ConfigureAwait(false);
    }

    public async Task Joiner(ITaskHandler taskHandler)
    {
      logger_.Log(LogLevel.Debug, $"Starting Joiner useCase");
      var resultId = taskHandler.ExpectedResults.Single();

      var restultsArray = new List<string>();

      foreach (var dependency in taskHandler.DataDependencies.Values)
      {
        var result = Encoding.ASCII.GetString(dependency);
        restultsArray.Add($"{result}_Joined");
      }

      await taskHandler.SendResult(resultId,
       restultsArray.SelectMany(s => Encoding.ASCII.GetBytes(s + "\n")).ToArray());
    }
  }
}
