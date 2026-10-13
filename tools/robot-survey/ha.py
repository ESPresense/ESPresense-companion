import json, os, sys, time, urllib.request, urllib.error, itertools
URL=os.environ.get("HA_MCP_URL") or sys.exit("set HA_MCP_URL to your Home Assistant MCP endpoint")
TOKEN=os.environ.get("HA_TOKEN")  # long-lived access token, if the endpoint wants one
_id=itertools.count(1)
_sess={'v':None}
def _post(payload, notify=False):
    # The gateway in front of HA 502s now and then; ride it out rather than kill a tour.
    for attempt in range(6):
        try: return _post1(payload, notify)
        except urllib.error.HTTPError as e:
            # 404 on a live session id means the server dropped the session (MCP spec): start a new one.
            # An HA restart also 404s the endpoint until the integration is back, so keep retrying.
            if e.code == 404 and payload.get('method') != 'initialize' and attempt < 5:
                _sess['v']=None
                try: init()
                except Exception: pass
            elif e.code < 500 or attempt == 5: raise
        except urllib.error.URLError:
            if attempt == 5: raise
        time.sleep(5*(attempt+1))

def _post1(payload, notify=False):
    data=json.dumps(payload).encode()
    h={'Content-Type':'application/json','Accept':'application/json, text/event-stream'}
    if _sess['v']: h['Mcp-Session-Id']=_sess['v']
    if TOKEN: h['Authorization']='Bearer '+TOKEN
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
