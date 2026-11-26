"""Generates monitoring/grafana/dashboards/synchub.json.

Edit the panels here, then run from the repo root:
    python monitoring/grafana/make_dashboard.py monitoring/grafana/dashboards/synchub.json
Grafana reloads the file within a few seconds. Editing the JSON by hand works too, but this keeps
the layout and styling consistent.
"""
import json, sys
P={"type":"prometheus","uid":"prometheus"}; L={"type":"loki","uid":"loki"}
panels=[]; pid=[0]; OFF=[0]
def nid(): pid[0]+=1; return pid[0]
def row(title,y): panels.append({"type":"row","title":title,"id":nid(),"collapsed":False,"gridPos":{"x":0,"y":y+OFF[0],"w":24,"h":1},"panels":[]})
def stat(title,expr,x,y,w=6,h=4,unit="short",thresholds=None,legend="",desc=""):
    panels.append({"type":"stat","title":title,"description":desc,"id":nid(),"datasource":P,"gridPos":{"x":x,"y":y+OFF[0],"w":w,"h":h},
      "targets":[{"refId":"A","datasource":P,"expr":expr,"legendFormat":legend,"instant":True}],
      "options":{"reduceOptions":{"calcs":["lastNotNull"]},"colorMode":"background","graphMode":"none","textMode":"value_and_name" if legend else "value"},
      "fieldConfig":{"defaults":{"unit":unit,"thresholds":{"mode":"absolute","steps":thresholds or [{"color":"green","value":None}]}},"overrides":[]}})
def ts(title,targets,x,y,w=12,h=8,unit="short",stack=False,desc="",draw="line"):
    panels.append({"type":"timeseries","title":title,"description":desc,"id":nid(),"datasource":P,"gridPos":{"x":x,"y":y+OFF[0],"w":w,"h":h},
      "targets":[{"refId":chr(65+i),"datasource":P,"expr":e,"legendFormat":lf} for i,(e,lf) in enumerate(targets)],
      "options":{"legend":{"displayMode":"list","placement":"bottom"},"tooltip":{"mode":"multi"}},
      "fieldConfig":{"defaults":{"unit":unit,"custom":{"drawStyle":draw,"fillOpacity":20 if stack else 5,"stacking":{"mode":"normal" if stack else "none"}}},"overrides":[]}})
def logs(title,expr,x,y,w=24,h=10,desc=""):
    panels.append({"type":"logs","title":title,"description":desc,"id":nid(),"datasource":L,"gridPos":{"x":x,"y":y+OFF[0],"w":w,"h":h},
      "targets":[{"refId":"A","datasource":L,"expr":expr}],
      "options":{"showTime":True,"wrapLogMessage":True,"sortOrder":"Descending","enableLogDetails":True,"dedupStrategy":"none"}})

red=[{"color":"green","value":None},{"color":"orange","value":180},{"color":"red","value":600}]
row("Is data flowing?",0)
stat("Time since last successful sync","max by (source) (synchub_sync_time_since_success_seconds)",0,1,w=8,unit="s",thresholds=red,legend="{{source}}",
     desc="Seconds since each source last synced successfully. The SyncStale alert fires above 10 minutes.")
stat("Failed runs (last hour)",'sum(increase(synchub_sync_runs_total{outcome="Failed"}[1h])) or vector(0)',8,1,w=5,
     thresholds=[{"color":"green","value":None},{"color":"red","value":1}])
stat("Records rejected (last hour)","sum(increase(synchub_records_rejected_total[1h])) or vector(0)",13,1,w=5,
     thresholds=[{"color":"green","value":None},{"color":"orange","value":1}])
stat("Rejection rate (15m)","(sum(rate(synchub_records_rejected_total[15m])) or vector(0)) / sum(rate(synchub_records_read_total[15m]))",18,1,w=6,unit="percentunit",
     thresholds=[{"color":"green","value":None},{"color":"orange","value":0.01},{"color":"red","value":0.05}],desc="Share of records read that were dead-lettered. Alert above 5%.")
row("Sync engine",5)
ts("Records merged per minute",[("sum by (source, operation) (rate(synchub_records_merged_total[5m])) * 60","{{source}} {{operation}}")],0,6,stack=True)
ts("Records rejected per minute, by error type",[("sum by (source, error_type) (rate(synchub_records_rejected_total[5m])) * 60","{{source}} {{error_type}}")],12,6,stack=True)
ts("Sync run duration (p95)",[("histogram_quantile(0.95, sum by (source, le) (rate(synchub_sync_duration_seconds_bucket[15m])))","{{source}}")],0,14,w=8,unit="s")
ts("Sync runs by outcome",[("sum by (source, outcome) (increase(synchub_sync_runs_total[5m]))","{{source}} {{outcome}}")],8,14,w=8,draw="bars",stack=True)
ts("Cycle step failures",[("sum by (step) (increase(synchub_cycle_step_failures_total[5m]))","{{step}}")],16,14,w=8,draw="bars",stack=True)
row("Exception queue and reconciliation (from SQL, every 2 minutes)",22)
stat("Open dead letters","sum(synchub_deadletters_open) or vector(0)",0,23,w=4,h=8,
     thresholds=[{"color":"green","value":None},{"color":"orange","value":1},{"color":"red","value":500}],
     desc="Records waiting for support: fix, replay or ignore them in the support dashboard.")
ts("Open dead letters by error type",[("sum by (source, error_type) (synchub_deadletters_open)","{{source}} {{error_type}}")],4,23,w=8,stack=True)
stat("Oldest open dead letter","max by (source) (synchub_deadletters_oldest_open_age_seconds)",12,23,w=4,h=8,unit="s",legend="{{source}}",
     thresholds=[{"color":"green","value":None},{"color":"orange","value":14400},{"color":"red","value":86400}],
     desc="DeadLettersAgeing fires above a day.")
stat("Reconciliation: unexplained records","synchub_reconciliation_unexplained",16,23,w=4,h=8,legend="{{source}} {{entity}}",
     thresholds=[{"color":"red","value":None},{"color":"green","value":0},{"color":"red","value":1}],
     desc="SourceTotal - InHub - Open - Ignored. Anything but 0 shows Investigate on the support dashboard.")
ts("Replayed records (1h)",[("sum by (source, outcome) (increase(synchub_replay_records_total[1h]))","{{source}} {{outcome}}")],20,23,w=4,h=8,draw="bars",stack=True,
   desc="Did the fixes support queued go through (resolved) or fail again?")
OFF[0]=9
row("Firm B API (seen from the worker)",22)
ts("Firm B requests per second, by status",[('sum by (http_response_status_code, error_type) (rate(http_client_request_duration_seconds_count{job="SyncHub.Worker", server_address="localhost", server_port="5070"}[5m]))',"{{http_response_status_code}} {{error_type}}")],0,23,w=8,stack=True,
   desc="Every attempt, including retries. 503s here are the mock's injected outages.")
ts("Firm B latency (p95)",[('histogram_quantile(0.95, sum by (le) (rate(http_client_request_duration_seconds_bucket{job="SyncHub.Worker", server_port="5070"}[5m])))',"p95")],8,23,w=8,unit="s")
ts("Resilience events (retries, circuit breaker)",[("sum by (event_name) (increase(resilience_polly_strategy_events_total{event_name!~\"PipelineExecuting|PipelineExecuted\"}[5m]))","{{event_name}}")],16,23,w=8,draw="bars",stack=True,
   desc="Polly events from the Firm B resilience pipeline: OnRetry, OnCircuitOpened, OnTimeout...")
row("APIs",31)
ts("Requests per second, by service and status",[('sum by (job, http_response_status_code) (rate(http_server_request_duration_seconds_count[5m]))',"{{job}} {{http_response_status_code}}")],0,32,stack=True)
ts("Latency p95, by route",[('histogram_quantile(0.95, sum by (job, http_route, le) (rate(http_server_request_duration_seconds_bucket[5m])))',"{{job}} {{http_route}}")],12,32,unit="s")
row("Logs (Loki)",40)
logs("Warnings and errors, all services",'{service_name=~"$service"} | severity_text=~"Warning|Error|Critical" | CorrelationId=~"$correlation"',0,41,h=10,
     desc="Expand a line to see Source, CorrelationId and SyncRunId. Paste a CorrelationId from the dashboard's run history into the variable above to see one run end to end.")
logs("All logs",'{service_name=~"$service"} | CorrelationId=~"$correlation"',0,51,h=12)

dash={"uid":"synchub-overview","title":"Merger Sync Hub","tags":["synchub"],"timezone":"browser","schemaVersion":41,"version":1,
 "refresh":"30s","time":{"from":"now-1h","to":"now"},"editable":True,"graphTooltip":1,
 "templating":{"list":[
   {"name":"service","label":"Service","type":"custom","query":"SyncHub.Worker,SyncHub.SupportApi,FirmB.MockApi,CorpDirectory.Api","multi":True,"includeAll":True,
    "allValue":".+","current":{"selected":True,"text":["All"],"value":["$__all"]},
    "options":[]},
   {"name":"correlation","label":"CorrelationId (regex)","type":"textbox","query":".*","current":{"text":".*","value":".*"}}]},
 "annotations":{"list":[]},"panels":panels}
json.dump(dash,open(sys.argv[1],"w"),indent=2)
