// -----------------------------------------------------------------------
//  <copyright file="SubscriptionCriteria.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Raven.Client.Documents.Replication.Messages;
using Raven.Client.Server;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Subscriptions
{
    public class SubscriptionState : IDatabaseTask
    {
        public SubscriptionCriteria Criteria { get; set; }
        public ChangeVectorEntry[] ChangeVector { get; set; }
        public long SubscriptionId { get; set; }
        public string SubscriptionName { get; set; }
        public DateTime TimeOfLastClientActivity { get; set; }
        public bool Disabled { get; set; }
        public Dictionary<string, long> LastEtagReachedInServer { get; set; }

        public ulong GetTaskKey()
        {
            return (ulong)SubscriptionId;
        }


        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(this.Criteria)] = new DynamicJsonValue
                {
                    [nameof(SubscriptionCriteria.Collection)] = Criteria.Collection,
                    [nameof(SubscriptionCriteria.Script)] = Criteria.Script,
                    [nameof(SubscriptionCriteria.IsVersioned)] = Criteria.IsVersioned
                },
                [nameof(ChangeVector)] = ChangeVector?.ToJson(),
                [nameof(SubscriptionId)] = SubscriptionId,
                [nameof(SubscriptionName)] = SubscriptionName,
                [nameof(TimeOfLastClientActivity)] = TimeOfLastClientActivity
            };
        }

        public static string GenerateSubscriptionItemKeyName(string databaseName, string subscriptionName)
        {
            return $"{SubscriptionPrefix(databaseName)}{subscriptionName}";
        }
        public static string GenerateSubscriptionItemNameFromId(string databaseName, long subscriptionId)
        {
            return $"{SubscriptionPrefix(databaseName)}{subscriptionId}";
        }

        public static string SubscriptionPrefix(string databaseName)
        {
            return $"{Helpers.ClusterStateMachineValuesPrefix(databaseName)}subscriptions/";
        }
    }
}