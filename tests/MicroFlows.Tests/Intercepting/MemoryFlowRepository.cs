﻿using MicroFlows.Domain.Interfaces;
using MicroFlows.Domain.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MicroFlows.Application.Helpers;
using FluentResults;

namespace MicroFlows.Tests.Intercepting;
internal class MemoryFlowRepository : IFlowRepository
{
    internal Dictionary<string, FlowStoreModel> _contextDictHistory = [];

    public Task<FlowContext> CreateFlowContext(IFlow flow, FlowParams flowParams)
    {
        var ctx = new FlowContext();
        ctx.Model.ImportFrom(flow);
        ctx.Params = flowParams;
        ctx.RefId = Guid.NewGuid().ToString();
        ctx.ExecutionResult.FlowState = Domain.Enums.FlowStateEnum.Start;

        var flowModel = new FlowStoreModel()
        {
            RefId = ctx.RefId,
            ExternalId = flowParams.ExternalId,
            FlowTypeName = flow.GetType().FullName!,
            ContextHistory = [ctx],
        };

        _contextDictHistory[ctx.RefId] = flowModel;
        return Task.FromResult(ctx);
    }

    public async Task<List<FlowContext>?> FindFlowHistory(FlowSearchQuery query)
    {
        if (query.RefId != null)
        {
            return await GetFlowHistory(query.RefId);
        }

        if (query.ExternalId != null)
        {
            var record = _contextDictHistory.Values.FirstOrDefault(f => f.ExternalId == query.ExternalId);
            return record?.ContextHistory;
        }

        return null;
    }

    public Task<List<FlowContext>> GetFlowHistory(string refId)
    {
        return Task.FromResult(_contextDictHistory[refId].ContextHistory);
    }

    public Task<FlowStoreModel> GetFlowModel(string refId)
    {
        return Task.FromResult(_contextDictHistory[refId]);
    }

    public Task SaveContextHistory(List<FlowContext> contextHistory)
    {
        var id = contextHistory.First().RefId;
        _contextDictHistory[id].ContextHistory = contextHistory;
        return Task.CompletedTask;
    }
}