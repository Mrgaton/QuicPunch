// Software-friendly renderer: simplify/cull geometry only after a gesture settles.
// During pan/zoom, copy a cached bitmap and draw constant-size markers.
const rad=Math.PI/180;
const project=(lon,lat)=>[(lon+180)/360,(1-Math.asinh(Math.tan(Math.max(-85.051129,Math.min(85.051129,lat))*rad))/Math.PI)/2];
let geography;
function polygonShapes(shape){
    return (shape.type==='Polygon'?[shape.coordinates]:shape.coordinates).map(polygon=>{
        const rings=polygon.map(r=>r.map(p=>project(...p)));let w=Infinity,n=Infinity,e=-Infinity,s=-Infinity;
        for(const ring of rings)for(const p of ring){w=Math.min(w,p[0]);e=Math.max(e,p[0]);n=Math.min(n,p[1]);s=Math.max(s,p[1]);}
        return {rings,w,n,e,s,cache:new Map()};
    });
}
function contains(shape,x,y){
    if(x<shape.w||x>shape.e||y<shape.n||y>shape.s)return false;
    const inside=ring=>{let hit=false;for(let i=0,j=ring.length-1;i<ring.length;j=i++){
        const a=ring[i],b=ring[j];if((a[1]>y)!==(b[1]>y)&&x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0])hit=!hit;
    }return hit;};
    return inside(shape.rings[0])&&!shape.rings.slice(1).some(inside);
}
function simplify(points,tolerance){
    if(points.length<5)return points;
    const keep=new Uint8Array(points.length),stack=[0,points.length-1],limit=tolerance*tolerance;
    keep[0]=keep[points.length-1]=1;
    while(stack.length){
        const end=stack.pop(),start=stack.pop(),a=points[start],b=points[end],dx=b[0]-a[0],dy=b[1]-a[1],length=dx*dx+dy*dy;
        let maximum=limit,index=-1;
        for(let i=start+1;i<end;i++){
            const p=points[i],t=length?Math.max(0,Math.min(1,((p[0]-a[0])*dx+(p[1]-a[1])*dy)/length)):0;
            const d=(p[0]-a[0]-t*dx)**2+(p[1]-a[1]-t*dy)**2;
            if(d>maximum){maximum=d;index=i;}
        }
        if(index>=0){keep[index]=1;stack.push(start,index,index,end);}
    }
    const result=points.filter((_,i)=>keep[i]);return result.length>=4?result:points;
}
async function basemap(){
    if(!geography)geography=fetch('./map-data',{credentials:'same-origin'}).then(r=>{if(!r.ok)throw new Error('No se pudo abrir el mapa local.');return r.json();}).then(data=>{
        return {shapes:data.countries.flatMap(polygonShapes),cities:data.cities.map(c=>({...c,p:project(...c.coordinates),textWidth:0})),
            communities:(data.communities||[]).map(c=>({...c,shapes:polygonShapes(c.geometry),p:project(...c.label),score:null}))};
    });
    return geography;
}
function pathFor(shape,level){
    if(shape.cache.has(level))return shape.cache.get(level);
    const path=new Path2D(),tolerance=.7/(256*2**level);
    for(const ring of shape.rings){let previous;for(const p of simplify(ring,tolerance)){
        if(!previous||Math.abs(previous[0]-p[0])>.5)path.moveTo(...p);else path.lineTo(...p);previous=p;
    }path.closePath();}
    if(shape.cache.size>=3)shape.cache.delete(shape.cache.keys().next().value);
    shape.cache.set(level,path);return path;
}
function color(value){
    const stops=[[0,37,68,118],[.35,31,151,171],[.7,210,200,76],[1,240,106,75]];
    let i=1;while(i<stops.length-1&&value>stops[i][0])i++;
    const a=stops[i-1],b=stops[i],t=(value-a[0])/(b[0]-a[0]);
    return [1,2,3].map(k=>Math.round(a[k]+(b[k]-a[k])*t));
}
export async function create(host,options={}){
    const canvas=host.querySelector('canvas'),ctx=canvas.getContext('2d',{alpha:false,willReadFrequently:true});
    const tooltip=host.querySelector('.map-tooltip'),scaleLabel=host.querySelector('.map-scale'),status=host.querySelector('.map-status');
    canvas.dataset.renderer='cpu-raster-cache';canvas.dataset.pixelRatio='1';
    let data;try{data=await basemap();}catch(e){status.textContent=e.message;return{update(){},dispose(){}};}
    const bitmap=document.createElement('canvas'),background=bitmap.getContext('2d',{alpha:false,willReadFrequently:true});
    let width=1,height=1,pixels=512,cx=.5,cy=.5,scene={markers:[]},cache,heatImage,heatBounds,heatBytes,primaryHeat,conditionalHeat;
    let showRouters=host.querySelector('[data-layer=routers]')?.checked??true,showHeat=true,showReferences=true,showCommunities=true,showModel=false,selectedCommunity='',heatSource='auto';
    let pointer,frame=0,settle=0,lastFrame=0,disposed=false,follow=true,fullDraws=0,previewDraws=0;
    const events=new AbortController(),signal=events.signal;
    const communitySelect=host.querySelector('[data-community]'),communityStatus=host.querySelector('[data-community-status]'),comparison=host.querySelector('[data-community-comparison]');
    for(const c of data.communities){const option=document.createElement('option');option.value=c.id;option.textContent=c.name;communitySelect?.append(option);}
    function area(west,south,east,north){
        const a=project(west,north),b=project(east,south);cx=(a[0]+b[0])/2;cy=(a[1]+b[1])/2;
        pixels=Math.min(maxPixels,Math.max(128,Math.min((width-40)/(b[0]-a[0]),(height-50)/(b[1]-a[1]))));follow=false;invalidate();
    }
    function spain(){area(-10,35.5,4.5,44.5);}
    function selectCommunity(id){
        selectedCommunity=id;const c=data.communities.find(c=>c.id===id);if(communitySelect)communitySelect.value=id;
        if(c){area(...c.bounds);if(communityStatus)communityStatus.textContent=`${c.name} · ${c.score===null?'Sin datos de calor':`índice visual ${c.score}/100`} · explorar esta región no la confirma como ubicación.`;}
        else{if(communityStatus)communityStatus.textContent='Todas las comunidades · el índice compara el color sobre el territorio, no una probabilidad.';spain();}
        canvas.dataset.community=id;invalidate();
    }
    function compareCommunities(){
        const h=scene.heat;
        for(const c of data.communities){
            c.score=null;if(!h||!heatBytes)continue;
            let total=0,count=0;
            const a=project(c.bounds[0],c.bounds[3]),b=project(c.bounds[2],c.bounds[1]);
            for(let iy=0;iy<16;iy++)for(let ix=0;ix<16;ix++){
                const x=a[0]+(ix+.5)/16*(b[0]-a[0]),y=a[1]+(iy+.5)/16*(b[1]-a[1]);
                if(!c.shapes.some(s=>contains(s,x,y)))continue;
                const hx=Math.floor((x-heatBounds[0][0])/(heatBounds[1][0]-heatBounds[0][0])*h.width),hy=Math.floor((y-heatBounds[0][1])/(heatBounds[1][1]-heatBounds[0][1])*h.height);
                total+=hx>=0&&hx<h.width&&hy>=0&&hy<h.height?heatBytes[hy*h.width+hx]:0;count++;
            }
            c.score=count?Math.round(total/count/255*100):0;
        }
        comparison?.replaceChildren(...[...data.communities].sort((a,b)=>(b.score??-1)-(a.score??-1)||a.name.localeCompare(b.name,'es')).map(c=>{
            const button=document.createElement('button'),name=document.createElement('span'),score=document.createElement('small');button.type='button';button.dataset.region=c.id;
            name.textContent=c.name;score.textContent=c.score===null?'Sin datos':`Índice ${c.score}/100`;button.append(name,score);button.addEventListener('click',()=>selectCommunity(c.id),{signal});return button;
        }));
        if(communityStatus&&selectedCommunity){const c=data.communities.find(c=>c.id===selectedCommunity);communityStatus.textContent=`${c.name} · ${c.score===null?'Sin datos de calor':`índice visual ${c.score}/100`} · no confirma tu ubicación.`;}
    }
    function configureHeat(){
        scene.heat=heatSource==='primary'?primaryHeat:heatSource==='conditional'?conditionalHeat:primaryHeat||conditionalHeat;
        heatImage=null;heatBytes=null;
        const h=scene.heat;
        if(h){
            heatBytes=typeof h.values==='string'?Uint8Array.from(atob(h.values),c=>c.charCodeAt(0)):h.values;
            heatImage=document.createElement('canvas');heatImage.width=h.width;heatImage.height=h.height;
            const target=heatImage.getContext('2d',{willReadFrequently:true}),image=target.createImageData(h.width,h.height);
            for(let i=0;i<heatBytes.length;i++){const p=heatBytes[i]*4,j=i*4;image.data[j]=palette[p];image.data[j+1]=palette[p+1];image.data[j+2]=palette[p+2];image.data[j+3]=palette[p+3];}
            target.putImageData(image,0,0);heatBounds=[project(h.west,h.north),project(h.east,h.south)];
        }
        canvas.dataset.heatSource=h?(h===conditionalHeat?'conditional':'primary'):'none';canvas.dataset.heatCells=String(heatBytes?.filter(v=>v>0).length||0);
        const caption=host.querySelector('[data-heat-caption]');if(caption)caption.textContent=!h?'Sin datos suficientes para colorear':h===conditionalHeat?'Afinidad condicional · coordenadas declaradas, sin verificar':'Afinidad de red dentro de las cotas verificadas';
        compareCommunities();
    }
    let localPlaces=[],localKey='',localRequest;
    const labelFor=place=>({name:place.displayName||place.name,p:project(place.position.longitude,place.position.latitude),zoom:0,textWidth:0});
    function loadLocalities(){
        if(disposed||Math.log2(pixels/256)<7)return;
        const lat=y=>Math.atan(Math.sinh(Math.PI*(1-2*y)))/rad;
        const south=Math.max(-85.051128,lat(cy+height/2/pixels)),north=Math.min(85.051128,lat(cy-height/2/pixels));
        const span=width/pixels*360;if(span>180||south>north)return;
        const west=(((cx-width/2/pixels)*360)%360+360)%360-180,east=west+span;
        const ranges=east>180?[[west,180],[-180,east-360]]:[[west,east]];
        const key=JSON.stringify([south,north,west,east].map(n=>Math.round(n*1000)/1000));if(key===localKey)return;
        localKey=key;localRequest?.abort();const controller=localRequest=new AbortController();
        Promise.all(ranges.map(async([w,e])=>{const response=await fetch(`./localities?south=${south}&north=${north}&west=${w}&east=${e}`,{signal:controller.signal,credentials:'same-origin'});if(!response.ok)throw Error('Catálogo local no disponible');return response.json();}))
            .then(results=>{if(disposed||controller!==localRequest)return;localPlaces=results.flat().map(labelFor);invalidate();})
            .catch(()=>{if(!disposed&&controller===localRequest)localPlaces=[];});
    }
    const maxPixels=options.streetTiles?2**27:2**22,tiles=new Map(),touches=new Map();
    let streets=false,pinch,tileErrors=false;
    function clearTiles(){
        for(const tile of tiles.values())if(tile.image){tile.image.onload=tile.image.onerror=null;if(tile.state==='loading')tile.image.src='';}
        tiles.clear();
    }
    function pumpTiles(){
        if(disposed||!streets)return;
        let active=[...tiles.values()].filter(t=>t.state==='loading').length;
        for(const tile of tiles.values()){
            if(active>=4)break;if(!tile.needed||tile.state!=='queued')continue;
            active++;tile.state='loading';const img=tile.image=new Image();
            img.referrerPolicy='strict-origin-when-cross-origin';img.decoding='async';
            const complete=state=>{if(disposed||tile.state!=='loading')return;tile.state=state;if(state==='error')tileErrors=true;invalidate(60);pumpTiles();};
            img.onload=()=>complete('ready');img.onerror=()=>complete('error');
            img.src=`https://tile.openstreetmap.org/${tile.z}/${tile.x}/${tile.y}.png`;
        }
    }
    function streetTiles(w,h){
        if(!streets)return;
        const z=Math.max(0,Math.min(19,Math.floor(Math.log2(pixels/256)))),n=2**z,size=pixels/n;
        // Only tiles intersecting the visible canvas. No background prefetch or zoom stacks.
        const x0=Math.floor((cx-width/2/pixels)*n),x1=Math.floor((cx+width/2/pixels)*n);
        const y0=Math.max(0,Math.floor((cy-height/2/pixels)*n)),y1=Math.min(n-1,Math.floor((cy+height/2/pixels)*n));
        for(const tile of tiles.values())tile.needed=false;
        for(let y=y0;y<=y1;y++)for(let worldX=x0;worldX<=x1;worldX++){
            const x=((worldX%n)+n)%n,key=`${z}/${x}/${y}`;
            let tile=tiles.get(key);if(!tile){tile={z,x,y,state:'queued'};tiles.set(key,tile);}tile.needed=true;
            if(tile.state==='ready')background.drawImage(tile.image,w/2+(worldX/n-cx)*pixels,h/2+(y/n-cy)*pixels,size+.5,size+.5);
        }
        for(const [key,tile]of tiles){
            if(!tile.needed&&(tile.state!=='ready'||tiles.size>96)){
                if(tile.image){tile.image.onload=tile.image.onerror=null;if(tile.state==='loading')tile.image.src='';}
                tiles.delete(key);
            }
        }
        pumpTiles();
    }
    const palette=new Uint8ClampedArray(256*4);
    for(let i=1;i<256;i++){const v=Math.sqrt((i-1)/254),rgb=color(v);palette.set([...rgb,Math.round(35+125*v)],i*4);}
    const position=p=>[(p[0]+Math.round(cx-p[0])-cx)*pixels+width/2,(p[1]-cy)*pixels+height/2];
    function scale(){
        const latitude=Math.atan(Math.sinh(Math.PI*(1-2*cy))),kmPerPixel=40030.2*Math.cos(latitude)/pixels;
        const target=90*kmPerPixel,magnitude=10**Math.floor(Math.log10(target));
        const km=[1,2,5,10].map(v=>v*magnitude).filter(v=>v<=target).pop()||magnitude;
        scaleLabel.style.width=`${km/kmPerPixel}px`;scaleLabel.textContent=km<1?`${Math.round(km*1000)} m`:`${Math.round(km)} km`;
        canvas.dataset.zoom=Math.log2(pixels/256).toFixed(2);
        status.textContent=streets?(tileErrors?'Algunas calles no están disponibles · mapa local conservado':`Calles en línea · zoom ${Math.log2(pixels/256).toFixed(1)}`):'Mapa local · CPU';
    }
    function markers(){
        if(scene.prior){const[x,y]=position(project(scene.prior.position.longitude,scene.prior.position.latitude));
            ctx.beginPath();ctx.moveTo(x,y-5);ctx.lineTo(x+5,y);ctx.lineTo(x,y+5);ctx.lineTo(x-5,y);ctx.closePath();ctx.fillStyle='#f5c968';ctx.fill();}
        const model=scene.modelPoint||scene.estimate;
        if(showModel&&model){const[x,y]=position(project(model.longitude,model.latitude));
            ctx.strokeStyle='#c4c8dc';ctx.lineWidth=2;ctx.beginPath();ctx.arc(x,y,7,0,Math.PI*2);ctx.moveTo(x-11,y);ctx.lineTo(x+11,y);ctx.moveTo(x,y-11);ctx.lineTo(x,y+11);ctx.stroke();}
        if(showReferences)for(const r of scene.references||[]){
            const[x,y]=position(project(r.position.longitude,r.position.latitude));if(x<0||y<0||x>width||y>height)continue;
            ctx.beginPath();ctx.arc(x,y,5,0,Math.PI*2);ctx.fillStyle=r.minimumMs==null?'#6c7f8c':'#9ce4cf';ctx.fill();ctx.strokeStyle='#132c32';ctx.lineWidth=2;ctx.stroke();
            if(r.minimumMs!=null&&pixels>=4096){ctx.font='11px system-ui';const text=`${r.name} · ${r.minimumMs.toFixed(1)} ms`,w=ctx.measureText(text).width+10;
                ctx.fillStyle='#10212be8';ctx.fillRect(x+7,y-9,w,19);ctx.fillStyle='#d9fff2';ctx.fillText(text,x+12,y+5);}
        }
        canvas.dataset.referenceMarkers=String(showReferences?(scene.references||[]).length:0);canvas.dataset.modelVisible=String(showModel&&!!model);
        if(!showRouters)return;
        for(const m of scene.markers){const[x,y]=position(m.p);if(x<0||y<0||x>width||y>height)continue;
            ctx.beginPath();ctx.arc(x,y,2.5,0,Math.PI*2);ctx.fillStyle='#7cddd0';ctx.fill();ctx.strokeStyle='#15232e';ctx.lineWidth=1;ctx.stroke();}
    }
    function preview(){
        const started=performance.now();
        ctx.setTransform(1,0,0,1,0,0);ctx.fillStyle='#14232e';ctx.fillRect(0,0,width,height);
        if(cache){const ratio=pixels/cache.pixels;ctx.imageSmoothingEnabled=false;
            ctx.drawImage(bitmap,width/2+(cache.cx-cx)*pixels-bitmap.width*ratio/2,height/2+(cache.cy-cy)*pixels-bitmap.height*ratio/2,bitmap.width*ratio,bitmap.height*ratio);}
        markers();scale();previewDraws++;canvas.dataset.previewDraws=String(previewDraws);canvas.dataset.lastPreviewMs=(performance.now()-started).toFixed(2);
    }
    function requestPreview(){
        if(frame||disposed)return;
        frame=requestAnimationFrame(t=>{frame=0;if(t-lastFrame<32){requestPreview();return;}lastFrame=t;preview();});
    }
    function full(){
        settle=0;if(disposed||pointer)return;
        const started=performance.now(),mx=Math.min(160,Math.round(width/4)),my=Math.min(110,Math.round(height/4));
        bitmap.width=width+mx*2;bitmap.height=height+my*2;
        const w=bitmap.width,h=bitmap.height,left=cx-w/2/pixels,right=cx+w/2/pixels,top=cy-h/2/pixels,bottom=cy+h/2/pixels;
        const offsets=right-left>1?[0]:[-1,0,1];
        const level=Math.max(0,Math.min(14,Math.floor(Math.log2(pixels/256))));
        background.setTransform(1,0,0,1,0,0);background.fillStyle='#14232e';background.fillRect(0,0,w,h);
        background.setTransform(pixels,0,0,pixels,w/2-cx*pixels,h/2-cy*pixels);
        background.fillStyle='#2c3a42';background.strokeStyle='#71818b';background.lineWidth=.65/pixels;
        let vertices=0;
        for(const shape of data.shapes){if(shape.s<top||shape.n>bottom)continue;
            for(const offset of offsets){if(shape.e+offset<left||shape.w+offset>right)continue;
                const path=pathFor(shape,level);background.save();background.translate(offset,0);background.fill(path,'evenodd');background.stroke(path);background.restore();vertices++;}}
        background.setTransform(1,0,0,1,0,0);
        streetTiles(w,h);
        if(showHeat&&heatImage){background.imageSmoothingEnabled=true;for(const offset of offsets){
            const a=heatBounds[0],b=heatBounds[1];
            background.drawImage(heatImage,w/2+(a[0]+offset-cx)*pixels,h/2+(a[1]-cy)*pixels,(b[0]-a[0])*pixels,(b[1]-a[1])*pixels);}}
        if(showCommunities){
            background.setTransform(pixels,0,0,pixels,w/2-cx*pixels,h/2-cy*pixels);
            for(const c of data.communities)for(const shape of c.shapes){
                if(shape.e<left||shape.w>right||shape.s<top||shape.n>bottom)continue;
                const path=pathFor(shape,level);background.strokeStyle=c.id===selectedCommunity?'#c3ffea':'#9fb5b9';background.lineWidth=(c.id===selectedCommunity?2.7:1.1)/pixels;
                if(c.id===selectedCommunity){background.fillStyle='#97ddc41c';background.fill(path,'evenodd');}background.stroke(path);
            }
            background.setTransform(1,0,0,1,0,0);
        }
        const boxes=[],zoom=Math.log2(pixels/256);background.font='11px system-ui';background.textBaseline='middle';
        const communityLabels=showCommunities?data.communities.map(c=>({name:c.name,p:c.p,zoom:5.1,textWidth:0})):[];
        const labels=[...communityLabels,...(scene.places||[]).map(c=>labelFor(c.place)),...localPlaces,...data.cities];
        for(const city of streets?communityLabels:labels){
            if(city.zoom>zoom+.6)continue;
            const x=w/2+(city.p[0]+Math.round(cx-city.p[0])-cx)*pixels,y=h/2+(city.p[1]-cy)*pixels;
            if(x<mx-20||y<my-20||x>w-mx+20||y>h-my+20)continue;
            city.textWidth ||= background.measureText(city.name).width+8;
            if(boxes.some(b=>x<b[2]&&x+city.textWidth>b[0]&&y-8<b[3]&&y+8>b[1]))continue;
            boxes.push([x,y-9,x+city.textWidth,y+9]);background.fillStyle='#14232ec0';background.fillRect(x+2,y-7,city.textWidth,14);
            background.fillStyle='#cedce4';background.fillText(city.name,x+5,y);
            if(boxes.length>=65)break;
        }
        cache={cx,cy,pixels};preview();fullDraws++;canvas.dataset.fullDraws=String(fullDraws);
        canvas.dataset.lastFullMs=(performance.now()-started).toFixed(2);canvas.dataset.visibleShapes=String(vertices);
        canvas.dataset.localityLabels=String(localPlaces.length);canvas.dataset.communityBoundaries=String(showCommunities?data.communities.length:0);canvas.dataset.heatVisible=String(showHeat&&!!heatImage);loadLocalities();
    }
    function invalidate(delay=0){clearTimeout(settle);if(!pointer&&!disposed)settle=setTimeout(full,delay);}
    function world(){cx=cy=.5;pixels=Math.max(128,Math.min(width-20,height-20));follow=false;invalidate();}
    function region(){
        if(!scene.heat){world();return;}
        const a=project(scene.heat.west,scene.heat.north),b=project(scene.heat.east,scene.heat.south);
        cx=(a[0]+b[0])/2;cy=(a[1]+b[1])/2;pixels=Math.max(128,Math.min((width-30)/Math.max(.001,b[0]-a[0]),(height-30)/Math.max(.001,b[1]-a[1])));
        follow=false;invalidate();
    }
    function focus(){
        const peaks=scene.heat?.peaks;
        if(!peaks?.length){
            const point=scene.center||scene.prior?.position;
            if(point){[cx,cy]=project(point.longitude,point.latitude);pixels=scene.radius?Math.min(maxPixels,Math.max(128,width*40030.2*Math.cos(point.latitude*rad)/(scene.radius*2.5))):256*2**10;follow=true;invalidate();return;}
            if(scene.heat)region();else spain();return;
        }
        const first=project(peaks[0].position.longitude,peaks[0].position.latitude),points=peaks.map(p=>{const q=project(p.position.longitude,p.position.latitude);q[0]+=Math.round(first[0]-q[0]);return q;});
        const west=Math.min(...points.map(p=>p[0])),east=Math.max(...points.map(p=>p[0])),north=Math.min(...points.map(p=>p[1])),south=Math.max(...points.map(p=>p[1]));
        cx=(west+east)/2;cy=(north+south)/2;
        const latitude=Math.atan(Math.sinh(Math.PI*(1-2*cy))),pad=Math.min(.5,Math.max(...peaks.map(p=>p.scaleKm))*2.8/(40030.2*Math.max(.15,Math.cos(latitude))));
        pixels=Math.max(128,Math.min(2**22,(width-30)/(east-west+pad*2),(height-30)/(south-north+pad*2)));
        follow=true;invalidate();
    }
    function zoom(factor,x=width/2,y=height/2){
        const next=Math.max(128,Math.min(maxPixels,pixels*factor));cx+=(x-width/2)*(1/pixels-1/next);cy+=(y-height/2)*(1/pixels-1/next);pixels=next;cy=Math.max(0,Math.min(1,cy));
        follow=false;tooltip.hidden=true;requestPreview();invalidate(140);
    }
    canvas.addEventListener('wheel',e=>{e.preventDefault();const r=canvas.getBoundingClientRect();zoom(Math.exp(-Math.max(-200,Math.min(200,e.deltaY))*.004),e.clientX-r.left,e.clientY-r.top);},{passive:false,signal});
    canvas.addEventListener('pointerdown',e=>{if(e.button!==0)return;clearTimeout(settle);follow=false;canvas.focus({preventScroll:true});canvas.setPointerCapture(e.pointerId);touches.set(e.pointerId,{x:e.clientX,y:e.clientY});pointer={x:e.clientX,y:e.clientY};if(touches.size===2){const[a,b]=[...touches.values()];pinch=Math.hypot(a.x-b.x,a.y-b.y);}tooltip.hidden=true;},{signal});
    canvas.addEventListener('pointermove',e=>{
        if(touches.has(e.pointerId))touches.set(e.pointerId,{x:e.clientX,y:e.clientY});
        if(touches.size===2){const[a,b]=[...touches.values()],distance=Math.hypot(a.x-b.x,a.y-b.y),rect=canvas.getBoundingClientRect();if(pinch>0)zoom(distance/pinch,(a.x+b.x)/2-rect.left,(a.y+b.y)/2-rect.top);pinch=distance;return;}
        if(pointer){cx=Math.max(-.2,Math.min(1.2,cx-(e.clientX-pointer.x)/pixels));cy=Math.max(0,Math.min(1,cy-(e.clientY-pointer.y)/pixels));pointer={x:e.clientX,y:e.clientY};requestPreview();return;}
        const rect=canvas.getBoundingClientRect(),x=e.clientX-rect.left,y=e.clientY-rect.top;
        const item=showRouters?scene.markers.find(m=>{const p=position(m.p);return Math.hypot(p[0]-x,p[1]-y)<8;}):null;
        const reference=showReferences?(scene.references||[]).find(r=>{const p=position(project(r.position.longitude,r.position.latitude));return Math.hypot(p[0]-x,p[1]-y)<8;}):null;
        const px=cx+(x-width/2)/pixels,py=cy+(y-height/2)/pixels;
        const community=showCommunities?data.communities.find(c=>c.shapes.some(s=>contains(s,px,py))):null;
        const text=reference?`${reference.name} · ${reference.minimumMs==null?'Sin respuesta':`${reference.minimumMs.toFixed(2)} ms`} · ${reference.coordinatesVerified?'coordenadas verificadas':'coordenadas declaradas'}`:item?.label||(community?`${community.name} · ${community.score===null?'sin datos de calor':`índice visual ${community.score}/100`}`:null);
        tooltip.hidden=!text;if(text){tooltip.textContent=text;tooltip.style.left=`${Math.max(8,Math.min(width-260,x+10))}px`;tooltip.style.top=`${Math.max(8,y-35)}px`;}
    },{signal});
    const release=e=>{touches.delete(e.pointerId);pinch=null;pointer=touches.size?[...touches.values()][0]:null;if(canvas.hasPointerCapture(e.pointerId))canvas.releasePointerCapture(e.pointerId);if(!pointer)invalidate();};
    canvas.addEventListener('pointerup',release,{signal});canvas.addEventListener('pointercancel',release,{signal});canvas.addEventListener('pointerleave',()=>{tooltip.hidden=true;},{signal});
    canvas.addEventListener('keydown',e=>{
        if(['+','=','-','ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home'].includes(e.key))e.preventDefault();
        if(e.key==='+'||e.key==='=')zoom(1.5);else if(e.key==='-')zoom(1/1.5);else if(e.key==='Home')focus();
        else if(e.key.startsWith('Arrow')){follow=false;if(e.key==='ArrowLeft')cx-=60/pixels;if(e.key==='ArrowRight')cx+=60/pixels;if(e.key==='ArrowUp')cy-=60/pixels;if(e.key==='ArrowDown')cy+=60/pixels;requestPreview();invalidate(140);}
    },{signal});
    host.addEventListener('click',e=>{const action=e.target.closest('[data-map]')?.dataset.map;if(action==='focus')focus();if(action==='spain')selectCommunity('');if(action==='region')region();if(action==='in')zoom(1.5);if(action==='out')zoom(1/1.5);},{signal});
    host.addEventListener('change',e=>{
        if(e.target.matches('[data-community]'))selectCommunity(e.target.value);
        if(e.target.matches('[data-heat-source]')){heatSource=e.target.value;configureHeat();invalidate();}
        if(e.target.dataset.layer==='heat'){showHeat=e.target.checked;invalidate();}
        if(e.target.dataset.layer==='communities'){showCommunities=e.target.checked;invalidate();}
        if(e.target.dataset.layer==='references'){showReferences=e.target.checked;requestPreview();}
        if(e.target.dataset.layer==='model'){showModel=e.target.checked;requestPreview();}
        if(e.target.dataset.layer==='routers'){showRouters=e.target.checked;requestPreview();}
        if(e.target.dataset.layer==='streets'&&options.streetTiles){streets=e.target.checked;tileErrors=false;if(!streets)clearTiles();invalidate();}
    },{signal});
    const observer=new ResizeObserver(()=>{
        const rect=canvas.getBoundingClientRect(),initial=width===1;width=Math.max(1,Math.round(rect.width));height=Math.max(1,Math.round(rect.height));canvas.width=width;canvas.height=height;
        if(initial){if(scene.heat||scene.prior)focus();else{spain();follow=true;}}
        else if(selectedCommunity){const selected=data.communities.find(c=>c.id===selectedCommunity);if(selected)area(...selected.bounds);else invalidate();}
        else invalidate();
    });observer.observe(canvas);spain();follow=true;
    return{
        update(value){
            scene={...value,markers:value.markers.map(m=>({...m,p:project(m.x,m.y)}))};primaryHeat=value.heat;conditionalHeat=value.conditionalHeat;configureHeat();
            const point=value.modelPoint||value.estimate,warning=host.querySelector('[data-model-warning]');
            if(warning){const p=point&&project(point.longitude,point.latitude);warning.hidden=!p||data.shapes.some(s=>contains(s,...p));}
            if(value.reset){follow=true;cache=null;}
            tooltip.hidden=true;if(follow&&(value.heat||value.center||value.prior))focus();else invalidate();
        },
        locate(point,radius=10){[cx,cy]=project(point.longitude,point.latitude);pixels=Math.min(maxPixels,Math.max(128,width*40030.2*Math.cos(point.latitude*rad)/(radius*2.5)));follow=false;invalidate();},
        dispose(){disposed=true;events.abort();localRequest?.abort();observer.disconnect();clearTimeout(settle);cancelAnimationFrame(frame);clearTiles();for(const shape of [...data.shapes,...data.communities.flatMap(c=>c.shapes)])shape.cache.clear();}
    };
}
