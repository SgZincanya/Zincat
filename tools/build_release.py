from pathlib import Path
import json,zipfile,hashlib,re
root=Path(__file__).resolve().parents[1]
package=root/'Packages/io.github.sgzincanya.zincat'
manifest=json.loads((package/'package.json').read_text(encoding='utf-8-sig'))
version=manifest['version'];name=manifest['name']
assert re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?',version)
dist=root/'dist';dist.mkdir(exist_ok=True)
archive=dist/(name+'-'+version+'.zip')
files=sorted((p for p in package.rglob('*') if p.is_file()),key=lambda p:p.relative_to(package).as_posix())
assert not any('/UploadSettings/' in p.as_posix() or '/Clothing2.0/Cache/' in p.as_posix() for p in files)
assert not manifest.get('legacyFolders'), 'Never migrate whole folders containing user data'
for p in files:
 if p.suffix=='.cs':
  legacy='Assets/Zincat/'+p.relative_to(package).as_posix()
  assert legacy in manifest.get('legacyFiles',{}), 'Missing script migration: '+legacy
  guid=re.search(r'^guid: (\w+)',Path(str(p)+'.meta').read_text(),re.M)[1]
  assert manifest['legacyFiles'][legacy]==guid, 'Migration GUID mismatch: '+legacy
assert not any('/Cache/' in p or '/UploadSettings/' in p for p in manifest['legacyFiles'])
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
 for p in files:
  data=p.read_bytes()
  if p.suffix in {'.cs','.json','.yml'}:data=data.replace(b'\r\n',b'\n')
  assert not data.startswith(b'version https://git-lfs.github.com/spec/v1'), 'Run git lfs pull: '+str(p)
  info=zipfile.ZipInfo(p.relative_to(package).as_posix(),date_time=(1980,1,1,0,0,0))
  info.create_system=3;info.compress_type=zipfile.ZIP_DEFLATED;info.external_attr=0o100644<<16
  z.writestr(info,data,compress_type=zipfile.ZIP_DEFLATED,compresslevel=6)
entry=dict(manifest);entry['zipSHA256']=hashlib.sha256(archive.read_bytes()).hexdigest()
docs=root/'docs';docs.mkdir(exist_ok=True)
index_path=docs/'index.json'
index=json.loads(index_path.read_text(encoding='utf-8')) if index_path.exists() else {'name':'Zincat VPM Repository','author':'SgZincanya','id':'io.github.sgzincanya.zincat.repo','url':'https://raw.githubusercontent.com/SgZincanya/Zincat/main/docs/index.json','packages':{}}
index['packages'].setdefault(name,{'versions':{}})['versions'][version]=entry
index_path.write_text(json.dumps(index,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'version':version,'archive':str(archive),'bytes':archive.stat().st_size,'sha256':entry['zipSHA256'],'files':len(files)}))
