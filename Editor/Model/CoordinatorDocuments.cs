using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Coordinator.Editor
{
    public static class CoordinatorRunBy
    {
        public const string Executor = "executor";
        public const string Lead = "lead";
    }

    public static class CoordinatorDecisionStates
    {
        public const string Open = "open";
        public const string Decided = "decided";
    }

    public static class CoordinatorInputKinds
    {
        public const string Source = "source";
        public const string Output = "output";
        public const string Decision = "decision";
        public const string Resource = "resource";
    }

    public static class CoordinatorDeliveryStates
    {
        public const string InProgress = "inProgress";
        public const string Delivered = "delivered";
    }

    public static class CoordinatorVerdicts
    {
        public const string Returned = "returned";
        public const string Accepted = "accepted";
    }

    [Serializable]
    public sealed class CoordinatorProjectRecord
    {
        public int schemaVersion = 1;
        public List<CoordinatorDisciplineRecord> disciplines = new();
        public List<CoordinatorReservedPathRecord> reservedPaths = new();
        public List<CoordinatorVariableRecord> variables = new();
        public List<CoordinatorCheckRecord> checks = new();
        public List<CoordinatorRuleRecord> rules = new();
    }

    [Serializable]
    public sealed class CoordinatorDisciplineRecord
    {
        public string id = string.Empty;
        public string title = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorReservedPathRecord
    {
        public string pattern = string.Empty;
        public string reason;
    }

    [Serializable]
    public sealed class CoordinatorVariableRecord
    {
        public string id = string.Empty;
        public string description = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorCheckRecord
    {
        public string id = string.Empty;
        public string title = string.Empty;
        public string command;
        public string generated;
        public string runBy = CoordinatorRunBy.Executor;
        public string limits;
    }

    [Serializable]
    public sealed class CoordinatorRuleRecord
    {
        public string id = string.Empty;
        public string text = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorTaskRecord
    {
        public int schemaVersion = 1;
        public string id = string.Empty;
        public string title = string.Empty;
        public string lead = string.Empty;
        public List<CoordinatorDocumentRef> sources = new();
        public string targetBranch = string.Empty;
        public string integrationBranch;
        [JsonProperty("base")] public CoordinatorBaseRecord baseRevision = new();
        public List<CoordinatorReservedPathRecord> reservedPaths = new();
        public CoordinatorSignatureRecord approval;
        public CoordinatorCancellationRecord cancelled;
        public List<CoordinatorDecisionRecord> decisions = new();
        public List<CoordinatorForbiddenRecord> forbidden = new();
        public CoordinatorIntegrationRecord integration;
    }

    [Serializable]
    public sealed class CoordinatorDocumentRef
    {
        public string path = string.Empty;
        public List<string> sections;
        public List<string> requirements;
        public string note;
    }

    [Serializable]
    public sealed class CoordinatorBaseRecord
    {
        public string branch = string.Empty;
        public string commit;
    }

    [Serializable]
    public sealed class CoordinatorSignatureRecord
    {
        public string by = string.Empty;
        public string date = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorCancellationRecord
    {
        public string by = string.Empty;
        public string date = string.Empty;
        public string reason = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorDecisionRecord
    {
        public string id = string.Empty;
        public string question = string.Empty;
        public string owner = string.Empty;
        public string state = CoordinatorDecisionStates.Open;
        public string answer;
        public CoordinatorRecordLink record;
    }

    [Serializable]
    public sealed class CoordinatorRecordLink
    {
        public string path;
        public string section;
        public string layer;
    }

    [Serializable]
    public sealed class CoordinatorForbiddenRecord
    {
        public string text = string.Empty;
        public List<CoordinatorItemsRef> refs;
    }

    [Serializable]
    public sealed class CoordinatorItemsRef
    {
        public string path = string.Empty;
        public List<string> items = new();
    }

    [Serializable]
    public sealed class CoordinatorIntegrationRecord
    {
        public CoordinatorDependenciesRecord dependsOn = new();
        public List<CoordinatorRouteStepRecord> route = new();
        public List<CoordinatorAcceptanceRecord> acceptance = new();
        public string limits;
    }

    [Serializable]
    public sealed class CoordinatorDependenciesRecord
    {
        public List<string> decisions = new();
        public List<CoordinatorOutputRef> outputs = new();
    }

    [Serializable]
    public sealed class CoordinatorOutputRef
    {
        public string assignment = string.Empty;
        public string output = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorRouteStepRecord
    {
        public string text = string.Empty;
        public string check;
        public List<string> checks;
    }

    [Serializable]
    public sealed class CoordinatorAcceptanceRecord
    {
        public string id = string.Empty;
        public string text = string.Empty;
        public string command;
        public string check;
        public List<string> checks;
        public string testFilter;
        public string runBy = CoordinatorRunBy.Executor;
    }

    [Serializable]
    public sealed class CoordinatorAssignmentRecord
    {
        public int schemaVersion = 1;
        public string id = string.Empty;
        public string title = string.Empty;
        public List<string> disciplines = new();
        public CoordinatorExecutorRecord executor = new();
        public string goal = string.Empty;
        public List<CoordinatorDocumentRef> readFirst = new();
        public List<CoordinatorLayerRef> layers = new();
        public List<CoordinatorInputRecord> inputs = new();
        public List<CoordinatorOutputRecord> outputs = new();
        public List<string> writeZone = new();
        public CoordinatorDependenciesRecord dependsOn = new();
        public List<CoordinatorRouteStepRecord> route = new();
        public List<CoordinatorForbiddenRecord> forbidden = new();
        public List<CoordinatorAcceptanceRecord> acceptance = new();
        public CoordinatorDeliverySpecRecord delivery = new();
        public CoordinatorCancellationRecord cancelled;
    }

    [Serializable]
    public sealed class CoordinatorExecutorRecord
    {
        public string handle;
        public string branch = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorLayerRef
    {
        public string path = string.Empty;
        public string role = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorInputRecord
    {
        public string kind = string.Empty;
        public string path;
        public string assignment;
        public string output;
        public string decision;
        public string text;
        public string use;
    }

    [Serializable]
    public sealed class CoordinatorOutputRecord
    {
        public string id = string.Empty;
        public string kind = string.Empty;
        public List<string> paths = new();
        public string summary;
    }

    [Serializable]
    public sealed class CoordinatorDeliverySpecRecord
    {
        public List<string> report = new();
    }

    [Serializable]
    public sealed class CoordinatorDeliveryRecord
    {
        public int schemaVersion = 1;
        public string assignment = string.Empty;
        public string state = CoordinatorDeliveryStates.InProgress;
        public List<CoordinatorCheckResultRecord> checks = new();
        public List<CoordinatorContractRecord> contracts = new();
        public List<string> integrationRequirements = new();
        public List<CoordinatorUnverifiedRecord> unverified = new();
    }

    [Serializable]
    public sealed class CoordinatorCheckResultRecord
    {
        public string acceptance;
        public string check;
        public string command;
        public int? exitCode;
        public string result;
    }

    [Serializable]
    public sealed class CoordinatorContractRecord
    {
        public string entry = string.Empty;
        public string text = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorUnverifiedRecord
    {
        public string acceptance;
        public string text = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorReviewRecord
    {
        public int schemaVersion = 1;
        public string assignment = string.Empty;
        public List<CoordinatorReviewRoundRecord> rounds = new();
    }

    [Serializable]
    public sealed class CoordinatorReviewRoundRecord
    {
        public string revision = string.Empty;
        public string date = string.Empty;
        public List<CoordinatorReviewNoteRecord> reviews = new();
        public List<CoordinatorCheckResultRecord> checks = new();
        public List<CoordinatorFindingRecord> findings = new();
        public string verdict = string.Empty;
        public string acceptedBy;
        public string instructedBy;
    }

    [Serializable]
    public sealed class CoordinatorReviewNoteRecord
    {
        public string author = string.Empty;
        public string kind = string.Empty;
        public string text = string.Empty;
    }

    [Serializable]
    public sealed class CoordinatorFindingRecord
    {
        public string id = string.Empty;
        public string text = string.Empty;
        public string path;
    }

    [Serializable]
    public sealed class CoordinatorTaskReviewRecord
    {
        public int schemaVersion = 1;
        public string task = string.Empty;
        public List<CoordinatorMergeRecord> merges = new();
        public List<CoordinatorCheckResultRecord> checks = new();
        public CoordinatorRecordLink record;
    }

    [Serializable]
    public sealed class CoordinatorMergeRecord
    {
        public string assignment = string.Empty;
        public string revision = string.Empty;
        public string merge = string.Empty;
    }
}
