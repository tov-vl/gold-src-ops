"""Real PromQL and private Grafana rehearsal for the additive overview."""
import base64
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
MODEL = ROOT / 'ops/production/game-health-dashboard.json'


def fixtures():
    dashboard = json.loads(MODEL.read_bytes())
    panels = {p['id']: p for p in dashboard['panels']}
    defaults = {}
    def metric(group, name, value, extra=''):
        service, prefix = {
            'agent': ('game-event-agent', 'game_event_agent'),
            'backup': ('player-backup', 'player_backup'),
            'storage': ('game-host-storage', 'game_host_storage'),
            'resource': ('game-host-resource', 'game_host_resource')}[group]
        key = f'goldsrcops_{prefix}_{name}{{job="goldsrcops",service_name="goldsrcops-{service}-monitor"{extra}}}'
        defaults[key] = value
        return key
    observed, source, success = {}, {}, {}
    for group in ('agent', 'backup', 'storage', 'resource'):
        observed[group] = metric(group, 'observed_timestamp_seconds', '0+60x30')
        success[group] = metric(group, 'observation_success', '1+0x30')
        if group != 'backup':
            source[group] = metric(group, 'source_observed_timestamp_seconds', '0+60x30')
    records = ('goldsrcops_agent:observation_valid','goldsrcops_player_backup:observation_valid',
               'goldsrcops_storage:observation_valid','goldsrcops_resources:memory_valid','goldsrcops_resources:cpu_valid')
    defaults.update({r: '1+0x30' for r in records})
    for name, value in {'pending': 7, 'in_flight': 2, 'dead_letter': 1}.items():
        metric('agent', name, str(value)+'+0x30')
    # Known independent facts: a manual copy ten seconds old and uncertain weekly attempt.
    for name, value in {'last_success_timestamp_seconds':1190,'copy_present':1,'copy_source':2,
                        'last_attempt_status':9,'missed_cycle':1}.items():
        metric('backup', name, str(value)+'+0x30')
    for role in ('queue','spool','players','backup'):
        for name,value in {'role_available':1,'bytes_available':100,'bytes_total':200,
                           'inodes_supported':1,'inodes_available':10,'inodes_total':20}.items():
            metric('storage',name,str(value)+'+0x30',',role="'+role+'"')
    memory = metric('resource','memory_available','1+0x30')
    cpu = metric('resource','cpu_available','1+0x30')
    for name,value in {'memory_available_bytes':2000,'memory_total_bytes':4000,'swap_total_bytes':0,
                       'swap_free_bytes':0,'cpu_busy_ratio':0.2,'cpu_steal_ratio':0.05}.items():
        metric('resource',name,str(value)+'+0x30')
    tests = []
    def check(name, changes=None, statuses=(1,1,1,1,1), empty=False, facts=None):
        values = {} if empty else {**defaults, **(changes or {})}
        checks = [{'expr':panels[i]['targets'][0]['expr'],'eval_time':'20m',
                   'exp_samples':[{'labels':'{}','value':value}]} for i,value in enumerate(statuses,1)]
        for ident, expected in (facts or {}).items():
            checks.append({'expr':panels[ident]['targets'][0]['expr'],'eval_time':'20m',
                           'exp_samples':[{'labels':'{}','value':expected}] if expected is not None else []})
        tests.append({'name':name,'interval':'1m','input_series':[
            {'series':key,'values':value} for key,value in values.items() if value is not None],
            'promql_expr_test':checks})
    check('fresh-independent-observations', facts={10:10,11:2,12:9,13:1})
    check('all-missing-is-unknown',statuses=(0,0,0,0,0),empty=True)
    for group,indexes in [('agent',[0]),('backup',[1]),('storage',[2]),('resource',[3,4])]:
        for kind,value in [('stale','0+0x30'),('future','99999+0x30'),('missing',None)]:
            status=[1]*5
            for index in indexes:status[index]=0
            check(group+'-'+kind,{observed[group]:value},status)
        status=[1]*5
        for index in indexes:status[index]=0
        check(group+'-invalid',{success[group]:'0+0x30'},status)
        if group in source:
            check(group+'-source-stale',{source[group]:'0+0x30'},status)
    for i,record in enumerate(records):
        status=[1]*5;status[i]=0
        check('policy-stopped-'+str(i),{record:'1+0x18'},status)
    check('memory-unavailable-keeps-cpu',{memory:'0+0x30'},[1,1,1,0,1],facts={18:None})
    check('cpu-unavailable-keeps-memory',{cpu:'0+0x30'},[1,1,1,1,0],facts={20:None})
    role = next(k for k in defaults if 'storage_role_available' in k and 'role="players"' in k)
    check('missing-storage-role',{role:None},[1,1,0,1,1])
    for state,status in [('pending',2),('firing',3)]:
        check('weekly-'+state+'-manual-copy-retained',{
            'ALERTS{component="player-backup",alertname="PlayerBackupScheduleProblem",alertstate="'+state+'"}':'1+0x30'
        },[1,status,1,1,1],facts={10:10,11:2,12:9,13:1})
    # No shared invalid-envelope warning may incorrectly turn a valid CPU sample red.
    check('memory-warning-does-not-claim-cpu-problem',{
        'ALERTS{component="game-host-resources",alertname="GameHostMemoryLow",alertstate="firing"}':'1+0x30'
    },[1,1,1,3,1])
    return {'rule_files':[],'evaluation_interval':'1m','tests':tests}


def validate_rules(docker, mount, image, temporary):
    value = fixtures()
    path = Path(temporary) / 'overview-query-tests.json'
    path.write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
    path.chmod(0o644)
    docker('run','--rm','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges',
           '--tmpfs','/tmp:rw,noexec,nosuid,size=64m','--mount',mount(path,'/fixtures.json'),
           '--entrypoint','/bin/promtool',image,'test','rules','/fixtures.json')
    print('GAME_HEALTH_QUERIES='+str(len(value['tests']))+'_scenarios_passed',flush=True)


def check_stack(graf_url, query, request):
    model = json.loads(MODEL.read_bytes())
    headers={'Authorization':'Basic '+base64.b64encode(b'admin:fixture-monitor-only').decode(),
             'Content-Type':'application/json'}
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
    def api(method,path,value=None):
        body=json.dumps(value).encode() if value is not None else None
        with opener.open(urllib.request.Request(graf_url+path,data=body,headers=headers,method=method),timeout=10) as response:
            return json.loads(response.read(262144))
    uid=model['uid']
    api('POST','/api/dashboards/db',{'dashboard':model,'overwrite':False})
    try:
        actual=request(graf_url+'/api/dashboards/uid/'+uid,True)
        assert not actual['meta']['provisioned'] and not actual['dashboard']['editable']
        assert actual['dashboard']['panels']==model['panels']
        ids=set()
        for panel in model['panels']:
            assert panel['id'] not in ids
            ids.add(panel['id'])
            for target in panel.get('targets',[]):
                assert target['instant'] and not target['range']
                query(target['expr'])
                result=api('POST','/api/ds/query',{'from':'now-5m','to':'now','queries':[
                    {**target,'datasource':{'type':'prometheus','uid':'goldsrcops-prometheus'}}]})
                assert not result['results']['A'].get('error'), result
        print('GAME_HEALTH_STACK=real_Grafana_datasource_queries_and_model_passed',flush=True)
    finally:
        api('DELETE','/api/dashboards/uid/'+uid)
