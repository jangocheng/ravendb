﻿// -----------------------------------------------------------------------
//  <copyright file="DatabaseRequestDurationLastMinuteMin.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using Lextm.SharpSnmpLib;

using Raven.Database.Server.Tenancy;

namespace Raven.Database.Plugins.Builtins.Monitoring.Snmp.Objects.Database.Requests
{
	public class DatabaseRequestDurationLastMinuteMin : DatabaseScalarObjectBase
	{
		public DatabaseRequestDurationLastMinuteMin(string databaseName, DatabasesLandlord landlord, int index)
			: base(databaseName, landlord, "5.2.{0}.3.4.2.2", index)
		{
		}

		protected override ISnmpData GetData(DocumentDatabase database)
		{
			return new Gauge32(GetCount(database));
		}

		private static int GetCount(DocumentDatabase database)
		{
			var metricsCounters = database.WorkContext.MetricsCounters;
			var data = metricsCounters.RequestDurationLastMinute.GetData();
			return (int)data.Min;
		}
	}
}