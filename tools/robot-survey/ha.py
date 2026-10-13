import json, os, sys, urllib.request, itertools
URL=os.environ.get("HA_MCP_URL") or sys.exit("set HA_MCP_URL to your Home Assistant MCP endpoint")
_id=itertools.count(1)
_sess={'v':None}
def _post(payload, notify=False):
    data=json.dumps(payload).encode()
    h={'Content-Type':'application/json','Accept':'application/json, text/event-stream'}
    if _sess['v']: h['Mcp-Session-Id']=_sess['v']
    req=urllib.request.Request(URL,data=data,headers=h)
    with urllib.request.urlopen(req,timeout=120) as r:
        sid=r.headers.get('Mcp-Session-Id')
        if sid: _sess['v']=sid
        body=r.read().decode()
    if notify: return None
    # SSE or plain json
    for line in body.splitlines():
        if line.startswith('data: '): body=line[6:]
    return json.loads(body)

def init():
    _post({"jsonrpc":"2.0","id":next(_id),"method":"initialize",
           "params":{"protocolVersion":"2025-06-18","capabilities":{},
                     "clientInfo":{"name":"robot-survey","version":"1"}}})
    _post({"jsonrpc":"2.0","method":"notifications/initialized"}, notify=True)

def call(name, args):
    r=_post({"jsonrpc":"2.0","id":next(_id),"method":"tools/call",
             "params":{"name":name,"arguments":args}})
    if 'error' in r: raise RuntimeError(r['error'])
    c=r['result']['content'][0]['text']
    try: return json.loads(c)
    except Exception: return c

if __name__=='__main__':
    init()
    print(json.dumps(call("ha_get_state",{"entity_id":"vacuum.p10_pro_ultra","fields":["state"]}))[:300])
