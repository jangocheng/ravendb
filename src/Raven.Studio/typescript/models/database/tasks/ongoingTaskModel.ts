﻿/// <reference path="../../../../typings/tsd.d.ts"/>

abstract class ongoingTaskModel { 

    taskId: number;
    taskName = ko.observable<string>();
    taskType = ko.observable<Raven.Client.Server.Operations.OngoingTaskType>();
    responsibleNode = ko.observable<Raven.Client.Server.Operations.NodeId>();
    taskState = ko.observable<Raven.Client.Server.Operations.OngoingTaskState>();
    taskConnectionStatus = ko.observable<Raven.Client.Server.Operations.OngoingTaskConnectionStatus>(); // TODO: discuss this property...
    
    badgeText: KnockoutComputed<string>;
    badgeClass: KnockoutComputed<string>;

    protected initializeObservables() {
        
        this.badgeClass = ko.pureComputed(() => {
            if (this.taskState() === 'Enabled') {
                return "state-success";
            }

            if (this.taskState() === "Disabled") {
                return "state-danger";
            }

            if (this.taskState() === "PartiallyEnabled") {
                return "state-warning";
            }

            return "state-offline"; // ? 
        });

        this.badgeText = ko.pureComputed(() => {
            if (this.taskState() === 'Enabled') {
                return "Enabled";
            }

            if (this.taskState() === "Disabled") {
                return "Disabled";
            }
            if (this.taskState() === "PartiallyEnabled") {
                return "Partial";
            }
            
            return "Offline"; // ?
        });
    }

    protected update(dto: Raven.Server.Web.System.OngoingTask) {
        this.taskId = dto.TaskId;
        this.taskName(dto.TaskName);
        this.taskType(dto.TaskType);
        this.responsibleNode(dto.ResponsibleNode);
        this.taskState(dto.TaskState);
        this.taskConnectionStatus(dto.TaskConnectionStatus);
    }
}

export = ongoingTaskModel;