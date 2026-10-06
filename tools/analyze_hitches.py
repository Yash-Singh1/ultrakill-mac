#!/usr/bin/env python3
"""Summarize in-game slow frames and CPU samples from the same wall-clock interval."""
import argparse, bisect, collections, csv, datetime, json, xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser()
p.add_argument('--phases',type=Path)
p.add_argument('--toc',type=Path)
p.add_argument('--cpu',type=Path)
p.add_argument('--output',type=Path,default=ROOT/'reports/latest-hitch-analysis.json')
a=p.parse_args()
files=sorted((ROOT/'diagnostics').glob('phases-*.csv'))
if a.phases is None:
    if not files: raise SystemExit('No in-game phase capture exists yet.')
    a.phases=files[-1]
def utc(s): return datetime.datetime.fromisoformat(s.replace('Z','+00:00')).timestamp()
with a.phases.open() as f:
    reader=csv.DictReader(f)
    phase_names=reader.fieldnames[5:]
    rows=list(reader)
# A scene can replace the phase arrays while its loading frame is still executing.
# Such transition rows have invalid phase durations and must not be attributed.
invalid=[r for r in rows if sum(float(r[k]) for k in phase_names)>float(r['frame_ms'])*1.1]
rows=[r for r in rows if r not in invalid]
report={'phase_file':str(a.phases),'slow_frames':len(rows),'invalid_transition_rows_excluded':len(invalid),'warning':'Loading, menus, intentional waits and profiler overhead can also produce slow frames. Phase timings locate time spent; they do not establish the underlying cause.','scenes':{},'largest_frames':[]}
for scene in sorted({r['scene'] for r in rows}):
    subset=[r for r in rows if r['scene']==scene]
    dominant=collections.Counter(max(phase_names+['between_updates_ms'],key=lambda k:float(r[k])) for r in subset)
    report['scenes'][scene]={'slow_frames':len(subset),'max_frame_ms':max(float(r['frame_ms']) for r in subset),'dominant_phases':dict(dominant)}
samples=[];times=[]
if bool(a.toc)!=bool(a.cpu):raise SystemExit('Provide both --toc and --cpu.')
if a.cpu:
    toc=ET.parse(a.toc).getroot()
    start=utc(toc.findtext('./run/info/summary/start-date'))
    tree=ET.parse(a.cpu).getroot()
    refs={e.attrib['id']:e for e in tree.iter() if 'id' in e.attrib}
    def resolve(e):return refs[e.attrib['ref']] if 'ref' in e.attrib else e
    for row in tree.findall('.//row'):
        fields=list(row)
        if len(fields)!=7:continue
        thread=resolve(fields[1]).get('fmt','')
        if 'Main Thread' not in thread:continue
        t=start+int(resolve(fields[0]).text)/1e9
        weight=int(resolve(fields[5]).text)/1e6
        tagged=resolve(fields[6]);backtrace=tagged.find('backtrace')
        if backtrace is None:continue
        backtrace=resolve(backtrace)
        frames=[resolve(e).get('name','unknown') for e in backtrace.findall('frame')]
        samples.append((t,weight,frames))
    samples.sort(key=lambda s:s[0]);times=[s[0] for s in samples]
    report['cpu_file']=str(a.cpu);report['main_thread_samples']=len(samples)
for r in sorted(rows,key=lambda r:float(r['frame_ms']),reverse=True)[:20]:
    end=utc(r['utc']);total=float(r['frame_ms']);begin=end-total/1000
    phases=sorted([(k,float(r[k])) for k in phase_names+['between_updates_ms']],key=lambda kv:kv[1],reverse=True)
    frame={'utc':r['utc'],'scene':r['scene'],'frame_ms':total,'gc_collections_in_loop':int(r['gc_collections']),'phases_ms':dict(phases[:5])}
    if a.cpu:
        matched=samples[bisect.bisect_left(times,begin):bisect.bisect_right(times,end)]
        leaf=collections.Counter();inclusive=collections.Counter()
        for _,weight,stack in matched:
            if stack:leaf[stack[0]]+=weight
            for name in set(stack):inclusive[name]+=weight
        frame['matched_main_thread_samples']=len(matched)
        frame['cpu_leaf_weight_ms']=dict(leaf.most_common(10))
        frame['cpu_inclusive_weight_ms']=dict(inclusive.most_common(10))
    report['largest_frames'].append(frame)
a.output.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'report':str(a.output),'slow_frames':len(rows),'scenes':report['scenes']},indent=2))
