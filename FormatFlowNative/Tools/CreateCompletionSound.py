"""Create FormatFlow's original completion cue (no downloaded/system sound)."""
from pathlib import Path
import math, struct, wave
rate=44100
duration=.94
frames=[]
notes=[(0.,659.255,.28,.20,-.22),(.095,987.767,.30,.19,.20),(.19,1318.510,.33,.19,-.08),(.33,1975.533,.28,.105,.22)]
for n in range(round(rate*duration)):
 t=n/rate
 left=right=0.
 for start,freq,tau,level,pan in notes:
  dt=t-start
  if dt<0: continue
  attack=1-math.exp(-dt/0.006)
  env=attack*math.exp(-dt/tau)
  phase=2*math.pi*freq*dt
  tone=(math.sin(phase)+.18*math.sin(phase*2.005)+.055*math.sin(phase*3.01))*level*env
  left+=tone*math.sqrt((1-pan)/2)
  right+=tone*math.sqrt((1+pan)/2)
 # Brief rising digital shimmer beneath the bell notes.
 if t<.29:
  u=t/.29
  chirp=.043*math.sin(2*math.pi*(420*t+2200*t*t))*math.sin(math.pi*u)**2
  left+=chirp; right+=chirp*.85
 # Fully taper the tail so playback does not click at EOF.
 fade=min(1.,max(0.,(duration-t)/.095))
 frames.append((left*fade,right*fade))
peak=max(abs(v) for frame in frames for v in frame)
scale=.64/peak
path=Path(__file__).resolve().parents[1]/'Resources'/'Completion.wav'
with wave.open(str(path),'wb') as w:
 w.setnchannels(2);w.setsampwidth(2);w.setframerate(rate)
 w.writeframes(b''.join(struct.pack('<hh',round(l*scale*32767),round(r*scale*32767)) for l,r in frames))
print(path.name, 'original stereo cue:', duration, 'seconds')
