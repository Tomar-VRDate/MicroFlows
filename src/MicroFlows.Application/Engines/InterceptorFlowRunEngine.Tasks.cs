﻿using Castle.DynamicProxy;
using MicroFlows.Application.Helpers;
using MicroFlows.Domain.Interfaces;
using MicroFlows.Application.Engines;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using BlazorForms.Proxyma;
using System.Reflection;
using BlazorForms;
using System.Linq;
using MicroFlows.Domain.Models;
using System.Runtime.CompilerServices;
using MicroFlows.Application.Exceptions;
using MicroFlows.Domain.Enums;

namespace MicroFlows;
internal partial class InterceptorFlowRunEngine
{
    private async Task ProcessCallTask(string taskName, Func<Task> action)
    {
        _callIndex++;
        string taskNameId = $"{taskName}:{_callIndex}";
        _executionCallStack.Add(taskNameId);

        // check if we have context for that step in the storage
        //
        FlowContext currentTaskContext = GetTaskExecutionContextFromHistory(_context.RefId, taskNameId, _callIndex) ?? _context;
        // source context must not be changed, because it is in the cache
        currentTaskContext = TypeHelper.CloneObject(currentTaskContext);
        _context = currentTaskContext;

        bool isSkipMode = CompareExecutionCallStacksIdentical();

        if (isSkipMode)
        {
            // execute skip task - supply model that was on this step
            _flow.SetModel(currentTaskContext.Model);
            _flow.SetParams(currentTaskContext.Params);
        }
        else
        {
            // execute task

            // if flow changed model we should inherit this change
            // we make sure that model is a new instance
            _context.Model = _flow;
            var result = await ExecuteTask(action);

            // After Task Events
            TriggerEvents(taskName);

            var continueExecution = CanContinueExecution(result);

            if (continueExecution)
            {
                _context.CallStack.Add(taskNameId);
            }

            _context.CurrentTask = taskNameId;
            _context.ExecutionResult = result;

            // add context to history
            await AddContextToHistory(_context);

            if (!continueExecution)
            {
                _logger.LogInformation("ProcessCallTask - execution stopped");
                throw new FlowStopException();
            }
        }
    }

    private async Task AddContextToHistory(FlowContext context)
    {
        var copy = TypeHelper.CloneObject(context);
        _contextHistory.Add(copy);
    }

    private void TriggerEvents(string taskName)
    {
        //if (_loading)
        //{
        //    OnLoad?.Invoke(_flowBase, new FlowEventArgs { TaskName = taskName, Context = _context, Model = _context.Model });
        //    _loading = false;
        //}

        //if (_saving)
        //{
        //    OnSave?.Invoke(_flowBase, new FlowEventArgs { TaskName = taskName, Context = _context, Model = _context.Model });
        //    _saving = false;
        //}
    }

    private bool CanContinueExecution(TaskExecutionResult result)
    {
        return result.ResultState != ResultStateEnum.Fail && result.FlowState == FlowStateEnum.Continue;
    }

    private async Task<TaskExecutionResult> ExecuteTask(Func<Task> action)
    {
        var result = new TaskExecutionResult
        {
            ResultState = ResultStateEnum.Success,
            FlowState = FlowStateEnum.Continue
        };

        // clear _context
        if (_context.ExecutionResult != null)
        {
            _context.ExecutionResult.IsFormTask = false;
        }

        try
        {
            await action();
        }
        catch (AggregateException exc)
        {
            LogException(exc);
            var innerExc = exc.InnerException;

            if (innerExc != null)
            {
                result.ResultState = ResultStateEnum.Fail;
                result.ExceptionMessage = exc.Message;
                result.ExceptionStackTrace = exc.StackTrace;
            }
        }
        catch (TargetInvocationException exc)
        {
            LogException(exc);
            var innerExc = exc.InnerException as FlowStopException;

            if (innerExc != null)
            {
                result.ResultState = ResultStateEnum.Success;
                result.FlowState = FlowStateEnum.Stop;
                result.ExceptionMessage = innerExc.Message;
                result.ExceptionStackTrace = exc.StackTrace;
            }
            else
            {
                throw;
            }
        }
        catch (FlowStopException exc)
        {
            result.ResultState = TaskExecutionResultStateEnum.Success;
            result.FlowState = TaskExecutionFlowStateEnum.Stop;
            result.ExceptionMessage = exc.Message;
            result.ExceptionStackTrace = exc.StackTrace;
            _logStreamer.TrackException(exc);
        }
        catch (FlowFailedException exc)
        {
            LogException(exc);
            result.ResultState = TaskExecutionResultStateEnum.Fail;
            result.FlowState = TaskExecutionFlowStateEnum.Stop;
            result.ExceptionMessage = exc.Message;
            result.ExceptionStackTrace = exc.StackTrace;
            _logStreamer.TrackException(exc);
        }
        catch (Exception exc)
        {
            LogException(exc);
            result.ResultState = TaskExecutionResultStateEnum.Fail;
            result.ExceptionMessage = exc.Message;
            result.ExceptionStackTrace = exc.StackTrace;
            _logStreamer.TrackException(exc);
        }
        finally
        {
            // populate formId from formPlugin
            if (_context?.ExecutionResult?.IsFormTask == true)
            {
                result.FormId = _context.ExecutionResult.FormId;
                result.CallbackTaskId = _context.ExecutionResult.CallbackTaskId;
                result.FormState = _context.ExecutionResult.FormState;
                result.IsFormTask = _context.ExecutionResult.IsFormTask;
            }

            // populate rule validations
            result.TaskExecutionValidationIssues = _context.ExecutionResult.TaskExecutionValidationIssues;
        }

        return result;
    }

    private void LogException(Exception exc)
    {
        Console.WriteLine($"Exception thrown, message: {exc.Message}, stackTrace: {exc.StackTrace}");

        if (exc is AggregateException)
        {
            foreach (var e in (exc as AggregateException).InnerExceptions)
            {
                LogException(e);
            }
        }
        else if (exc is TargetInvocationException)
        {
            LogException(exc.InnerException);
        }
        else
        {
            if (exc.InnerException != null)
            {
                LogException(exc.InnerException);
            }
        }
    }

    private bool CompareExecutionCallStacksIdentical()
    {
        var left = _context.CallStack.Distinct().ToList();
        var right = _executionCallStack.Distinct().ToList();

        for (int i = 0; i < right.Count(); i++)
        {
            if (left.Count() <= i || right[i] != left[i])
            {
                return false;
            }
        }

        return true;
    }

    private FlowContext GetTaskExecutionContextFromHistory(string refId, string taskName, int callIndex)
    {
        var records = _contextHistory;

        for (int i = 0; i < records.Count; i++)
        {
            //if (records[i].CurrentTask == taskName && callIndex >= i)
            if (records[i].CurrentTask == taskName)
            {
                var startPosition = i;

                // iterate to last task with the same name
                while ((startPosition + 1) < records.Count)
                {
                    startPosition++;

                    if (records[startPosition].CurrentTask != taskName)
                    {
                        startPosition--;
                        break;
                    }
                }

                return records[startPosition];
            }
        }

        return null;
    }
}
