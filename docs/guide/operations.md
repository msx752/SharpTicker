# Running in production

This page was split into three:

- [Deploying](deploying.md): several instances, instance names, clocks, scheduler options, start modes and upgrades.
- [Failure handling](failure-modes.md): what happens to jobs when an instance stops, how a job is claimed and put back, outcomes that cannot be stored, when a job can run more than once, and the exception handler.
- [Tracing and logs](observability.md): activities, tags and the log entries.

## Where the sections went

| Section | Now on |
|---|---|
| <a id="several-instances"></a>Several instances | [Deploying](deploying.md#several-instances) |
| <a id="instance-names"></a>Instance names | [Deploying](deploying.md#instance-names) |
| <a id="when-an-instance-stops"></a>When an instance stops | [Failure handling](failure-modes.md#when-an-instance-stops) |
| <a id="how-a-job-is-claimed"></a>How a job is claimed | [Failure handling](failure-modes.md#how-a-job-is-claimed) |
| <a id="a-claim-whose-job-does-not-start"></a>A claim whose job does not start | [Failure handling](failure-modes.md#a-claim-whose-job-does-not-start) |
| <a id="run-now-when-the-job-cannot-start"></a>"Run now" when the job cannot start | [Failure handling](failure-modes.md#run-now-when-the-job-cannot-start) |
| <a id="writes-of-a-running-job"></a>Writes of a running job | [Failure handling](failure-modes.md#writes-of-a-running-job) |
| <a id="updating-a-stored-job"></a>Updating a stored job | [Failure handling](failure-modes.md#updating-a-stored-job) |
| <a id="when-an-outcome-cannot-be-stored"></a>When an outcome cannot be stored | [Failure handling](failure-modes.md#when-an-outcome-cannot-be-stored) |
| <a id="while-older-instances-still-run"></a>While older instances still run | [Deploying](deploying.md#while-older-instances-still-run) |
| <a id="when-a-job-can-run-more-than-once"></a>When a job can run more than once | [Failure handling](failure-modes.md#when-a-job-can-run-more-than-once) |
| <a id="clocks"></a>Clocks | [Deploying](deploying.md#clocks) |
| <a id="scheduler-options"></a>Scheduler options | [Deploying](deploying.md#scheduler-options) |
| <a id="start-modes"></a>Start modes | [Deploying](deploying.md#start-modes) |
| <a id="handling-failures"></a>Handling failures | [Failure handling](failure-modes.md#handling-failures) |
| <a id="dashboard-failures"></a>Dashboard failures | [Tracing and logs](observability.md#dashboard-failures) |
| <a id="scheduler-log-entries"></a>Scheduler log entries | [Tracing and logs](observability.md#scheduler-log-entries) |
| <a id="tracing"></a>Tracing | [Tracing and logs](observability.md#tracing) |

<script>
(function () {
  var moved = {
    "several-instances": "deploying", "instance-names": "deploying", "clocks": "deploying",
    "scheduler-options": "deploying", "start-modes": "deploying", "while-older-instances-still-run": "deploying",
    "when-an-instance-stops": "failure-modes", "how-a-job-is-claimed": "failure-modes",
    "a-claim-whose-job-does-not-start": "failure-modes", "run-now-when-the-job-cannot-start": "failure-modes",
    "writes-of-a-running-job": "failure-modes", "updating-a-stored-job": "failure-modes",
    "when-an-outcome-cannot-be-stored": "failure-modes", "when-a-job-can-run-more-than-once": "failure-modes",
    "handling-failures": "failure-modes",
    "dashboard-failures": "observability", "scheduler-log-entries": "observability", "tracing": "observability"
  };
  var anchor = location.hash.slice(1);
  if (moved.hasOwnProperty(anchor)) location.replace(moved[anchor] + ".html#" + anchor);
})();
</script>
