import math
# Corkscrew tail along the diagonal, drawn as a 2D helix projection.
def helix(x0,y0,x1,y1, turns, amp0, amp1, n=400):
    L=math.hypot(x1-x0,y1-y0); ux,uy=(x1-x0)/L,(y1-y0)/L; vx,vy=-uy,ux
    pts=[]
    for i in range(n+1):
        t=i/n; a=amp0+(amp1-amp0)*t
        ph=2*math.pi*turns*t
        s=math.sin(ph); c=math.cos(ph)
        x=x0+ux*L*t+vx*a*s; y=y0+uy*L*t+vy*a*s
        pts.append((x,y,c,t))
    return pts
pts=helix(150,150,238,238,3.6,9,6)
# split into front (cos>0) and back segments
def segs(front):
    out=[];cur=[]
    for x,y,c,t in pts:
        if (c>=0)==front: cur.append((x,y,t))
        else:
            if len(cur)>1: out.append(cur)
            cur=[]
    if len(cur)>1: out.append(cur)
    return out
def pathd(seg): return "M"+" L".join(f"{x:.1f},{y:.1f}" for x,y,t in seg)
def width(seg,base): return base*(1-0.55*seg[len(seg)//2][2])
back="".join(f'<path d="{pathd(s)}" stroke="#1d3d17" stroke-width="{width(s,19)+5:.1f}" fill="none" stroke-linecap="round"/><path d="{pathd(s)}" stroke="#3b7a2c" stroke-width="{width(s,19):.1f}" fill="none" stroke-linecap="round"/>' for s in segs(False))
front="".join(f'<path d="{pathd(s)}" stroke="#1d3d17" stroke-width="{width(s,19)+5:.1f}" fill="none" stroke-linecap="round"/><path d="{pathd(s)}" stroke="#6cc04a" stroke-width="{width(s,19):.1f}" fill="none" stroke-linecap="round"/><path d="{pathd(s)}" stroke="#b8f08f" stroke-width="{width(s,19)*0.28:.1f}" fill="none" stroke-linecap="round" opacity="0.8" transform="translate(-2,-2)"/>' for s in segs(True))
ex,ey,_,_=pts[-1]
svg=f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
<defs>
 <radialGradient id="glass" cx="0.38" cy="0.32" r="0.8"><stop offset="0" stop-color="#f3fbff"/><stop offset="0.6" stop-color="#bfe3f6"/><stop offset="1" stop-color="#7fb9da"/></radialGradient>
 <linearGradient id="brass" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe39a"/><stop offset="0.45" stop-color="#e0a83a"/><stop offset="1" stop-color="#8f5b14"/></linearGradient>
 <linearGradient id="skin" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7fd25a"/><stop offset="1" stop-color="#3f8a2e"/></linearGradient>
 <clipPath id="lens"><circle cx="104" cy="104" r="80"/></clipPath>
 <clipPath id="head"><path d="M52,92 C52,60 76,48 102,48 C128,48 152,60 152,92 C152,114 126,142 102,150 C78,142 52,114 52,92 Z"/></clipPath>
 <pattern id="scales" width="12" height="10" patternUnits="userSpaceOnUse" patternTransform="rotate(35)">
   <path d="M0,10 A6,6 0 0 1 12,10" fill="none" stroke="#2f6b22" stroke-width="1.6"/>
   <path d="M-6,5 A6,6 0 0 1 6,5 M6,5 A6,6 0 0 1 18,5" fill="none" stroke="#2f6b22" stroke-width="1.6"/>
 </pattern>
</defs>
<!-- tail / handle (behind the ring) -->
{back}
{front}
<circle cx="{ex:.1f}" cy="{ey:.1f}" r="4.5" fill="#6cc04a" stroke="#1d3d17" stroke-width="2.5"/>
<!-- glass -->
<circle cx="104" cy="104" r="80" fill="url(#glass)"/>
<g clip-path="url(#lens)">
  <!-- lizard body -->
  <ellipse cx="128" cy="162" rx="58" ry="34" transform="rotate(38 128 162)" fill="url(#skin)" stroke="#1d3d17" stroke-width="4"/>
  <ellipse cx="128" cy="162" rx="58" ry="34" transform="rotate(38 128 162)" fill="url(#scales)" opacity="0.7"/>
  <!-- ears -->
  <circle cx="62" cy="60" r="14" fill="#26232b"/><circle cx="62" cy="60" r="6.5" fill="#e9c9b8"/>
  <circle cx="142" cy="60" r="14" fill="#26232b"/><circle cx="142" cy="60" r="6.5" fill="#e9c9b8"/>
  <!-- badger head -->
  <path d="M52,92 C52,60 76,48 102,48 C128,48 152,60 152,92 C152,114 126,142 102,150 C78,142 52,114 52,92 Z" fill="#f6f1e7"/>
  <g clip-path="url(#head)">
    <ellipse cx="78" cy="96" rx="15" ry="52" transform="rotate(-24 78 96)" fill="#26232b"/>
    <ellipse cx="126" cy="96" rx="15" ry="52" transform="rotate(24 126 96)" fill="#26232b"/>
  </g>
  <path d="M52,92 C52,60 76,48 102,48 C128,48 152,60 152,92 C152,114 126,142 102,150 C78,142 52,114 52,92 Z" fill="none" stroke="#26232b" stroke-width="4"/>
  <!-- lizard eyes -->
  <circle cx="82" cy="98" r="9.5" fill="#ffc531" stroke="#5a3b00" stroke-width="1.5"/>
  <ellipse cx="82" cy="98" rx="2.6" ry="7.5" fill="#111"/>
  <circle cx="79" cy="94" r="2.4" fill="#fff"/>
  <circle cx="122" cy="98" r="9.5" fill="#ffc531" stroke="#5a3b00" stroke-width="1.5"/>
  <ellipse cx="122" cy="98" rx="2.6" ry="7.5" fill="#111"/>
  <circle cx="119" cy="94" r="2.4" fill="#fff"/>
  <!-- nose + grin -->
  <ellipse cx="102" cy="138" rx="10" ry="7" fill="#1a1a1f"/>
  <ellipse cx="99" cy="135.5" rx="3" ry="1.8" fill="#888"/>
  <!-- glass glare -->
  <path d="M50,70 A62,62 0 0 1 86,36" fill="none" stroke="#fff" stroke-width="7" stroke-linecap="round" opacity="0.55"/>
</g>
<!-- brass ring -->
<circle cx="104" cy="104" r="80" fill="none" stroke="#5c3a0a" stroke-width="22"/>
<circle cx="104" cy="104" r="80" fill="none" stroke="url(#brass)" stroke-width="15"/>
<!-- little paws gripping the rim -->
<g stroke="#1d3d17" stroke-width="2.5">
 <g transform="translate(66,176) rotate(28)">
  <path d="M-7,6 l-2,9 l5,-6 z M0,7 l0,10 l3,-9 z M7,6 l3,9 l-1,-10 z" fill="#f1ead6" stroke-width="1.5"/>
  <ellipse cx="0" cy="0" rx="13" ry="10" fill="#6cc04a"/>
 </g>
 <g transform="translate(110,186) rotate(4)">
  <path d="M-7,6 l-2,9 l5,-6 z M0,7 l0,10 l3,-9 z M7,6 l3,9 l-1,-10 z" fill="#f1ead6" stroke-width="1.5"/>
  <ellipse cx="0" cy="0" rx="13" ry="10" fill="#6cc04a"/>
 </g>
</g>
</svg>'''
open("tove.svg","w").write(svg)
