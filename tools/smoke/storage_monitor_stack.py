"""Independent storage policy expectations and actual private telemetry rehearsal."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / 'ops/production/observability'
PREFIX = 'goldsrcops_game_host_storage_'
LABELS = 'job="goldsrcops",service_name="goldsrcops-game-host-storage-monitor"'
ROLES = ('queue','spool','players','backup')
ALERTS = ('GameHostStorageMonitorUnavailable','GameHostStorageObservationInvalid',
          'GameHostStorageBytesLow','GameHostStorageInodesLow')


def fixtures():
    annotations, current = {}, None
    for line in (OBS/'rules/game-host-storage.yml').read_text(encoding='utf-8').splitlines():
        if '      - alert: ' in line:
            current=line.split(': ',1)[1];annotations[current]={}
        elif current and line.strip().startswith(('summary:','description:','runbook_url:')):
            key,value=line.strip().split(': ',1);annotations[current][key]=json.loads(value)
    defaults={name:'0+60x30' for name in ('observed_timestamp_seconds','source_observed_timestamp_seconds')}
    defaults['observation_success']='1+0x30'
    for role in ROLES:
        for name,value in {'role_available':1,'bytes_total':100*1024**3,'bytes_available':30*1024**3,
                           'inodes_supported':1,'inodes_total':1000000,'inodes_available':300000}.items():
            defaults[name+':'+role]=str(value)+'+0x30'
    tests=[]
    def case(name,changes,checks,empty=False):
        inputs=[]
        for key,values in ({} if empty else {**defaults,**changes}).items():
            if values is None:continue
            metric,_,role=key.partition(':')
            labels=LABELS+(',role="'+role+'"' if role else '')
            inputs.append({'series':PREFIX+metric+'{'+labels+'}','values':values})
        rules=[]
        for minute,expected in checks:
            for alert in ALERTS:
                rules.append({'eval_time':str(minute)+'m','alertname':alert,'exp_alerts':[
                    {'exp_labels':{'severity':'warning','component':'game-host-storage'},'exp_annotations':annotations[alert]}] if alert in expected else []})
        tests.append({'name':name,'interval':'1m','input_series':inputs,'alert_rule_test':rules})
    monitor,invalid,space,inodes=ALERTS
    case('healthy',{},[(20,[])])
    case('missing-monitor-holds',{},[(4,[]),(5,[monitor])],empty=True)
    case('frozen-heartbeat',{'observed_timestamp_seconds':'0+0x30'},[(3,[]),(8,[]),(9,[monitor])])
    case('future-heartbeat',{'observed_timestamp_seconds':'99999+0x30'},[(5,[monitor])])
    case('invalid-snapshot-suppresses-old-full-values',{'observation_success':'0+0x30','bytes_available:queue':'0+0x30'},[(5,[invalid])])
    case('old-source',{'source_observed_timestamp_seconds':'0+0x30'},[(9,[invalid])])
    case('future-source',{'source_observed_timestamp_seconds':'99999+0x30'},[(5,[invalid])])
    case('unavailable-role-suppresses-retained-capacity',{'role_available:queue':'0+0x30','bytes_available:queue':'0+0x30'},[(5,[invalid])])
    case('missing-role',{'role_available:queue':None},[(5,[invalid])])
    case('all-role-availability-missing',{ 'role_available:'+role:None for role in ROLES},[(5,[invalid])])
    case('bytes-exact-10-percent-and-2gib-are-healthy',{'bytes_total:queue':str(20*1024**3)+'+0x30','bytes_available:queue':str(2*1024**3)+'+0x30'},[(20,[])])
    case('bytes-relative-low',{'bytes_available:queue':str(9*1024**3)+'+0x30'},[(4,[]),(5,[space])])
    case('bytes-absolute-low',{'bytes_total:queue':str(4*1024**3)+'+0x30','bytes_available:queue':str(1024**3)+'+0x30'},[(5,[space])])
    case('bytes-full',{'bytes_available:queue':'0+0x30'},[(5,[space])])
    case('inode-exact-thresholds-healthy',{'inodes_total:players':'100000+0x30','inodes_available:players':'10000+0x30'},[(20,[])])
    case('inode-relative-low',{'inodes_available:players':'90000+0x30'},[(4,[]),(5,[inodes])])
    case('inode-absolute-low',{'inodes_total:players':'20000+0x30','inodes_available:players':'9999+0x30'},[(5,[inodes])])
    case('inode-full',{'inodes_available:players':'0+0x30'},[(5,[inodes])])
    case('inode-unsupported-masks-retained-zero',{'inodes_supported:players':'0+0x30','inodes_available:players':'0+0x30'},[(20,[])])
    case('transient-recovers-before-hold',{'bytes_available:queue':'0+0x3 '+str(30*1024**3)+'+0x26'},[(3,[]),(4,[]),(10,[])])
    case('fires-and-recovers',{'bytes_available:queue':'0+0x6 '+str(30*1024**3)+'+0x23'},[(5,[space]),(7,[])])
    case('invalid-recovers',{'observation_success':'0+0x6 1+0x23'},[(5,[invalid]),(7,[])])
    case('partial-unavailable-does-not-hide-other-full-role',{'role_available:queue':'0+0x30','bytes_available:spool':'0+0x30'},[(5,[invalid,space])])
    return {'rule_files':['/rules/game-host-storage.yml'],'evaluation_interval':'1m','tests':tests}


def validate_rules(docker,mount,image,temporary):
    value=fixtures();path=Path(temporary)/'storage-rule-tests.json'
    path.write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8');path.chmod(0o644)
    docker('run','--rm','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges',
           '--tmpfs','/tmp:rw,noexec,nosuid,size=64m','--mount',mount(OBS/'rules','/rules'),
           '--mount',mount(path,'/fixtures.json'),'--entrypoint','/bin/promtool',image,'test','rules','/fixtures.json')
    print('STORAGE_RULES='+str(len(value['tests']))+'_scenarios_passed',flush=True)


def check_stack(collector_url,graf_url,query,request,wait):
    import base64
    import urllib.request
    spec=importlib.util.spec_from_file_location('storage_stack_fixture',ROOT/'tools/smoke/storage-monitor.py')
    fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(fixture)
    monitor,contract=fixture.monitor,fixture.contract
    dashboard=json.loads((ROOT/'ops/production/storage-dashboard.json').read_bytes());uid=dashboard['uid']
    headers={'Authorization':'Basic '+base64.b64encode(b'admin:fixture-monitor-only').decode(),'Content-Type':'application/json'}
    def api(method,path,value=None):
        body=json.dumps(value).encode() if value is not None else None
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(urllib.request.Request(graf_url+path,data=body,headers=headers,method=method),timeout=5) as response:
            return json.loads(response.read(262144))
    port=int(collector_url.rsplit(':',1)[1]);real=monitor.transport.http.client.HTTPConnection
    def publish(kind=None):
        fixture.NOW=dt.datetime.now(contract.UTC);value=fixture.snapshot()
        if kind=='invalid':value=None
        elif kind=='role':value['roles']['queue']={'available':False}
        elif kind=='full':value['roles']['queue']['bytesAvailable']=0
        elif kind=='inode':value['roles']['players'].update(inodesTotal=None,inodesAvailable=None)
        with mock.patch.object(monitor.transport.http.client,'HTTPConnection',lambda address,target_port,timeout:real('127.0.0.1',port,timeout=timeout)):
            monitor.transport.publish('127.0.0.1',monitor.payload(monitor.observe(value,fixture.NOW),fixture.NOW))
    label='{job="goldsrcops",service_name="goldsrcops-game-host-storage-monitor",role="queue"}'
    def current(metric):return query(PREFIX+metric+label)
    api('POST','/api/dashboards/db',{'dashboard':dashboard,'overwrite':False})
    try:
        publish();wait(lambda:len(query('goldsrcops_storage:role_valid'))==4)
        actual=request(graf_url+'/api/dashboards/uid/'+uid,True)
        assert actual['dashboard']['uid']==uid and not actual['meta']['provisioned']
        for panel in dashboard['panels']:
            for target in panel.get('targets',[]):assert query(target['expr'])
        publish('full');wait(lambda:current('bytes_available') and current('bytes_available')[0]['value'][1]=='0')
        wait(lambda:query('ALERTS{alertname="GameHostStorageBytesLow",alertstate="pending"}'))
        publish('role');wait(lambda:query('ALERTS{alertname="GameHostStorageObservationInvalid",alertstate="pending"}'))
        wait(lambda:not query('ALERTS{alertname="GameHostStorageBytesLow"}'))
        available_expr=dashboard['panels'][3]['targets'][0]['expr']
        assert all(point['metric'].get('role')!='queue' for point in query(available_expr))
        publish('invalid');wait(lambda:not query('goldsrcops_storage:observation_valid'))
        assert not query(available_expr)
        publish('inode');wait(lambda:len(query(PREFIX+'inodes_supported{job="goldsrcops",role="players"}'))==1)
        inode_expr=dashboard['panels'][5]['targets'][0]['expr']
        wait(lambda:all(point['metric'].get('role')!='players' for point in query(inode_expr)))
        publish();wait(lambda:len(query('goldsrcops_storage:role_valid'))==4 and not query('ALERTS{component="game-host-storage"}'))
        print('STORAGE_STACK=OTLP_Grafana_full_partial_invalid_inode_recovery_passed',flush=True)
    finally:
        api('DELETE','/api/dashboards/uid/'+uid)
