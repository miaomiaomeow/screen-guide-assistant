import io, json, sys, unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent))
from guide_core import *
from guide_mcp import McpSession, content

class Fake:
 def __init__(self):
  self.t=Target(1,1,'1','class','app','title',Rect(0,0,400,400)); self.control=Control('item','Find','button',True,Rect(20,20,40,30)); self.captures=[]; self.visible=True
 def list_windows(self): return [self.t]
 def current_target(self,t): return t
 def foreground(self,t): return self.visible
 def unchanged(self,s): return self.visible
 def capture_controls(self,t): self.captures.append('controls'); return Snapshot(token(),t,t.bounds,t.bounds,b'',(self.control,))
 def capture(self,t,read_controls=True): self.captures.append('hybrid' if read_controls else 'image'); return Snapshot(token(),t,t.bounds,t.bounds,b'jpeg',(self.control,) if read_controls else ())
 def confirm_control(self,s,c): return True
 def confirm_image(self,s,b): return True
 def clear_overlay(self): pass
 def show_frame(self,f): self.frame=f

class Tests(unittest.TestCase):
 def start(self,mode):
  b=Fake(); r=GuideRuntime(b); targets=r.handle({'command':'list_targets'})['targets']; r.handle({'command':'bind_target','targetId':targets[0]['targetId']}); session=r.handle({'command':'codex_start','question':'where','mode':mode}); return b,r,session['sessionId']
 def test_catalog(self): self.assertEqual(len(tool_catalog()),8)
 def test_controls_never_capture_image(self):
  b,r,s=self.start('controls'); view=r.handle({'command':'codex_read','sessionId':s}); self.assertEqual(b.captures,['controls']); self.assertEqual(view['images'],[]); self.assertEqual(view['controls'][0]['id'],'item')
 def test_hybrid_includes_both(self):
  b,r,s=self.start('hybrid'); v=r.handle({'command':'codex_read','sessionId':s}); self.assertEqual(b.captures,['hybrid']); self.assertTrue(v['images']); self.assertTrue(v['controls'])
 def test_direct_image(self):
  b,r,s=self.start('direct_image'); r.handle({'command':'codex_read','sessionId':s}); self.assertEqual(b.captures,['image'])
 def test_fallback(self):
  b,r,s=self.start('image_on_failure'); v=r.handle({'command':'codex_read','sessionId':s}); v=r.handle({'command':'codex_point','sessionId':s,'snapshotId':v['snapshotId'],'action':'not_found','confidence':0.0}); self.assertEqual(b.captures,['controls','image']); self.assertTrue(v['images'])
 def test_click_and_old_snapshot(self):
  b,r,s=self.start('controls'); v=r.handle({'command':'codex_read','sessionId':s}); req={'command':'codex_point','sessionId':s,'snapshotId':v['snapshotId'],'action':'point','candidateId':'item','confidence':.99}; r.handle(req); r.observe('clicked'); self.assertEqual(r.guide.memory[0]['event'],'clicked')
  with self.assertRaises(GuideError): r.handle(req)
 def test_disabled(self):
  b,r,s=self.start('controls'); b.control=Control('item','Find','button',False,Rect(20,20,40,30)); v=r.handle({'command':'codex_read','sessionId':s})
  with self.assertRaises(GuideError): r.handle({'command':'codex_point','sessionId':s,'snapshotId':v['snapshotId'],'action':'point','candidateId':'item','confidence':.99})
 def test_wrong_target(self):
  b,r,s=self.start('controls'); b.visible=False
  with self.assertRaises(GuideError): r.handle({'command':'codex_read','sessionId':s})
 def test_letterbox(self):
  t=Target(1,1,'1','c','app','',Rect(0,0,400,200)); shot=Snapshot('a',t,t.bounds,t.bounds,b'img')
  with self.assertRaises(GuideError): map_box(shot,[0,0,40,40])
  self.assertEqual(map_box(shot,[100,300,200,400]),Rect(40,20,40,40))
 def test_schema_rejects_bad_params(self):
  with self.assertRaises(GuideError): validate_request('screen_guide_status',{'unexpected':1})
 def test_image_native_content(self):
  import base64
  v=content({'ok':True,'images':[{'data':base64.b64encode(b'x').decode(),'mimeType':'image/jpeg'}]}); self.assertEqual(v['content'][-1]['type'],'image'); self.assertNotIn('data',v['content'][0]['text'])
 def test_handshake(self):
  out=io.BytesIO(); session=McpSession(out); session.receive(json.dumps({'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{}}})); session.receive(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})); session.receive(json.dumps({'jsonrpc':'2.0','id':2,'method':'tools/list'})); session.close(); messages=[json.loads(x) for x in out.getvalue().splitlines()]; self.assertEqual(len(messages[-1]['result']['tools']),8)
if __name__=='__main__': unittest.main()
