"""Verify migrated first-run defaults and real restore commands on an owned SQL instance.

No production connection argument exists. Reuse the maintained lifecycle harness's
pinned image, baseline verification, Docker environment and owner-checked cleanup.
"""
from pathlib import Path
import runpy,subprocess,os,uuid,secrets,tempfile,time,json,re,signal,shutil
repo=Path(__file__).resolve().parents[2]
h=runpy.run_path(str(repo/'Tests/LifecycleSqlChecks/run.py'))
h['verify_baseline']()
owner=uuid.uuid4(); name='fmsc-level1-'+owner.hex
private=Path(tempfile.mkdtemp(prefix='fmsc-backup-safety-sql-'))
shared=private/owner.hex;shared.mkdir();shared.chmod(0o777)
state=private/'owner.json';state.write_text(json.dumps({'owner':str(owner),'container':name}))
pwd='A!'+secrets.token_urlsafe(32)+'9z'
env=h['docker_env']();env['SQLCMDPASSWORD']=pwd
cmd=h['docker_command']()
def run(args,timeout=240,cwd=repo,childenv=env):
 p=subprocess.run(args,cwd=cwd,env=childenv,capture_output=True,text=True,timeout=timeout)
 if p.returncode:
  (private/'failure.log').write_text((p.stdout+p.stderr).replace(pwd,'[REDACTED]'))
  (private/'failure.log').chmod(0o600)
  raise RuntimeError('Audit fixture command failed; private diagnostic retained at '+str(private/'failure.log'))
 return p.stdout
counter=0
def sql(db,text):
 global counter
 counter+=1;path=private/f'sql-{counter}.sql';path.write_text(text);path.chmod(0o444)
 run(cmd+['cp',str(path),name+':/tmp/audit.sql'])
 return run(cmd+['exec','--env','SQLCMDPASSWORD',name,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-I','-d',db,'-i','/tmp/audit.sql'])
def interrupted(n,f):raise SystemExit(128+n)
signal.signal(signal.SIGTERM,interrupted);signal.signal(signal.SIGINT,interrupted)
try:
 credentials=private/'sql.env';credentials.write_text('ACCEPT_EULA=Y\nMSSQL_SA_PASSWORD='+pwd+'\n');credentials.chmod(0o600)
 run(cmd+['run','-d','--name',name,'--label',h['LABEL']+'='+str(owner),'--env-file',str(credentials),'--memory','3g','--cpus','2','-p','127.0.0.1::1433','--mount',f'type=bind,src={shared},dst={shared}',h['IMAGE']],300)
 for attempt in range(120):
  p=subprocess.run(cmd+['exec','--env','SQLCMDPASSWORD',name,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],env=env,capture_output=True,timeout=10)
  if p.returncode==0:break
  time.sleep(1)
 else:raise RuntimeError('Owned SQL readiness failed')
 port=run(cmd+['port',name,'1433/tcp']).strip()
 if not re.fullmatch(r'127\.0\.0\.1:[0-9]+',port):raise RuntimeError('SQL is not exclusively loopback-bound')
 base=f'Server=127.0.0.1,{port.split(":")[1]};User ID=sa;Password={pwd};Encrypt=False;TrustServerCertificate=True;Connect Timeout=20;Pooling=False;'
 sql('master',f"EXEC sys.sp_addextendedproperty @name=N'FMSC_SafetyChecksOwner',@value=N'{owner.hex}';")
 database='FMSC_BackupAudit_'+owner.hex
 run(['dotnet','tool','run','sqlpackage','--','/Action:Publish','/SourceFile:'+str(repo/'Tests/LifecycleSqlChecks/Baseline/schema.dacpac'),'/TargetConnectionString:'+base+'Initial Catalog='+database+';','/p:CreateNewDatabase=True'],240,repo/'Tests/LifecycleSqlChecks')
 sql(database,"DECLARE @Owner uniqueidentifier='"+str(owner)+"';EXEC sys.sp_addextendedproperty @name=N'FMSC_Level1Owner',@value=@Owner;")
 sql(database,(repo/'Tests/LifecycleSqlChecks/Baseline/Initialize.sql').read_text())
 for migration in sorted((repo/'Migrations').glob('*.sql'),key=lambda p:int(p.stem)):
  sql(database,'SET XACT_ABORT ON;BEGIN TRANSACTION;\n'+migration.read_text(encoding='utf-8-sig')+'\nCOMMIT TRANSACTION;')
 child=env.copy();child.update(FMSC_SAFETY_CONNECTION=base+'Initial Catalog=master;',FMSC_SAFETY_OWNER=owner.hex,FMSC_SAFETY_ROOT=str(shared),FMSC_SAFETY_CONTAINER=name,FMSC_SAFETY_TEMPLATE=database)
 child.pop('DOCKER_CONTEXT',None)
 # Host and SQL container use different UIDs; temporary directories must be writable by both.
 # Private ancestor directories and the owner-labelled container bound the
 # shared fixture. SQL's mssql UID must create and delete disposable files.
 os.umask(0)
 print(run(['dotnet','run','--project',str(repo/'Tests/SafetySqlChecks/SafetySqlChecks.csproj'),'-c','Release','-v:q','--','--backup-recovery'],240,childenv=child),flush=True)
 print('PASS: owned fresh-template and SQL recovery checks completed; no school data was accessed',flush=True)
finally:
 h['remove_owned'](state)
 if credentials.exists(): credentials.unlink()
 print('CLEANUP: owner-checked disposable SQL container removed',flush=True)
 if not (private/'failure.log').exists(): shutil.rmtree(private)
