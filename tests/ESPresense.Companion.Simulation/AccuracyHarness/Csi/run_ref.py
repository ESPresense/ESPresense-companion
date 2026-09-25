import os, ecf1
OUT="corpus"; os.makedirs(OUT,exist_ok=True)
boards={0:"esp32",1:"esp32-s3"}
rates=[0,10,20,30,50,100]; scenes={0:"vacant",1:"occupied-still"}
rows=[]
for bid,bname in boards.items():
    for rate in rates:
        for sid,sname in scenes.items():
            p=os.path.join(OUT,f"{bname}_{rate:03d}hz_{sname}.ecf1")
            m=ecf1.gen_fixture(p,bid,rate,sid)
            h,counts,stat=ecf1.validate(p)  # round-trip parse every file
            duty=1-(stat[4]/stat[3]) if stat[3] else 0   # completed/attempted
            csidrop=(stat[8]/stat[6]) if stat[6] else 0   # dropped/expected
            rows.append((bname,rate,sname,os.path.getsize(p),duty,csidrop,stat[1],stat[2]))
# print an illustrative knee table (occupied-still rows)
print(f"{'board':10} {'rate':>4} {'scene':14} {'bytes':>6} {'bleDutyLoss':>11} {'csiDrop':>7} {'minHeap':>8} {'jitterUs':>8}")
for r in rows:
    if r[2]!='occupied-still': continue
    print(f"{r[0]:10} {r[1]:>4} {r[2]:14} {r[3]:>6} {r[4]*100:>10.1f}% {r[5]*100:>6.1f}% {r[6]:>8} {r[7]:>8}")
# derive the illustrative knee per board (<15% duty loss)
print("\nIllustrative measured-knee per board (highest rate with BLE duty loss <15%):")
for bid,bname in boards.items():
    ok=[r[1] for r in rows if r[0]==bname and r[2]=='occupied-still' and r[4]<0.15]
    print(f"  {bname:10} knee = {max(ok)} Hz" if ok else f"  {bname:10} knee = none")
print(f"\nGenerated+validated {len(rows)} ECF1 files in ./{OUT}/  (every file round-trip parsed OK)")
