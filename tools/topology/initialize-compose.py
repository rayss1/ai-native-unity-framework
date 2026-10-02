#!/usr/bin/env python3
"""Generate container peer keys and static per-process machine table under artifacts."""
import json, pathlib, subprocess, xml.etree.ElementTree as ET
root = pathlib.Path(__file__).resolve().parents[2]
run = root / 'artifacts' / 'topology-compose'
run.mkdir(parents=True, exist_ok=True)
public_dir = run / 'public'
private_dir = run / 'private'
public_dir.mkdir(exist_ok=True)
private_dir.mkdir(exist_ok=True)
private_dir.chmod(0o700)
services = [('gate','Gate',1,1101,11), ('player','Player',2,1102,12), ('lobby','Lobby',3,1103,13), ('match','Match',4,1104,14), ('coordinator','Coordinator',5,1105,15), ('battle-1','Battle',100,1106,16), ('battle-2','Battle',101,1107,17), ('acceptance','Client',99,1099,19)]
peers=[]
for name,role,pid,scene,ip in services:
    service_dir=private_dir/name
    service_dir.mkdir(exist_ok=True)
    service_dir.chmod(0o700)
    private=service_dir/'service.private.pem'; public=public_dir/(name+'.public.pem')
    if not private.exists():
        subprocess.run(['openssl','genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:2048','-out',str(private)],check=True)
        subprocess.run(['openssl','pkey','-in',str(private),'-pubout','-out',str(public)],check=True)
        private.chmod(0o600)
    peers.append(dict(Id=name,Role=role,ProcessId=pid,SceneId=scene,PublicKeyFile='/run/topology/public/'+public.name))
private=private_dir/'player'/'player-ticket.private.pem'
if not private.exists():
    subprocess.run(['openssl','genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:2048','-out',str(private)],check=True)
    subprocess.run(['openssl','pkey','-in',str(private),'-pubout','-out',str(public_dir/'player-ticket.public.pem')],check=True)
    private.chmod(0o600)
(public_dir/'peers.json').write_text(json.dumps(peers,indent=2))
ns={'f':'http://fantasy.net/config'}
ET.register_namespace('',ns['f'])
tree=ET.parse(root/'infrastructure/topology/Fantasy.config')
machines=tree.find('f:server/f:machines',ns)
machines.clear()
processes=tree.find('f:server/f:processes',ns)
for name,role,pid,scene,ip in services:
    ET.SubElement(machines,'{'+ns['f']+'}machine',dict(id=str(pid),outerIP=f'172.28.0.{ip}',outerBindIP=f'172.28.0.{ip}',innerBindIP=f'172.28.0.{ip}'))
    for proc in processes:
        if proc.attrib['id']==str(pid): proc.attrib['machineId']=str(pid)
tree.write(public_dir/'Fantasy.config',encoding='utf-8',xml_declaration=True)
print(run)
