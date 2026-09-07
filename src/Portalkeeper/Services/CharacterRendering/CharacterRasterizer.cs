using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using Portalkeeper.Models;
using SkiaSharp;

namespace Portalkeeper.Services.CharacterRendering;

internal static class CharacterRasterizer
{
    public static byte[] Render(string clientFolder,ArmoryCharacter character,CancellationToken token)
    {
        using var assets=new ClientAssets(clientFolder,token);
        var scene=CharacterScene.Resolve(assets,character,token);
        var body=new ClientMesh(assets,scene.Model,scene.Textures,scene.Visible,token);
        var meshes=new List<ClientMesh>{body};
        foreach(var a in scene.Attachments)
        {
            token.ThrowIfCancellationRequested();
            if(!body.Attachments.TryGetValue(a.Point,out var point))throw new System.IO.InvalidDataException("Equipment attachment unavailable.");
            var textures=new Dictionary<int,ClientTexture>();
            if(a.Texture.Length>0)textures[2]=ClientTexture.Decode(assets.Read(a.Texture));
            meshes.Add(new ClientMesh(assets,a.Model,textures,null,token,point));
        }
        const int width=480,height=640;
        float angle=20*MathF.PI/180;var forward=new Vector3(MathF.Cos(angle),MathF.Sin(angle),0);
        // M2 is Z-up: camera right = up cross camera-to-viewer, not its negative.
        var right=Vector3.Cross(Vector3.UnitZ,forward);
        var min=new Vector2(float.MaxValue);var max=new Vector2(float.MinValue);
        foreach(var m in meshes)foreach(int i in m.Surfaces.SelectMany(s=>s.Indices).Distinct())
        {
            var v=m.Positions[i];var p=new Vector2(Vector3.Dot(v,right),v.Z);min=Vector2.Min(min,p);max=Vector2.Max(max,p);
        }
        var range=max-min;float scale=MathF.Min((width-32)/MathF.Max(range.X,.01f),(height-32)/MathF.Max(range.Y,.01f));var center=(max+min)/2;
        var output=new byte[width*height*4];var depth=Enumerable.Repeat(float.NegativeInfinity,width*height).ToArray();
        var light=ClientLighting.Load(assets);
        // Complete opaque depth before drawing static additive material geometry.
        foreach(bool additive in new[]{false,true})
        foreach(var mesh in meshes)
        {
            var xy=new Vector2[mesh.Positions.Length];var z=new float[xy.Length];var lighting=new Vector3[xy.Length];
            for(int i=0;i<xy.Length;i++)
            {
                var v=mesh.Positions[i];xy[i]=new((Vector3.Dot(v,right)-center.X)*scale+width/2f,height/2f-(v.Z-center.Y)*scale);z[i]=Vector3.Dot(v,forward);
                lighting[i]=light.Shade(mesh.Normals[i]);
            }
            foreach(var surface in mesh.Surfaces)
            {
                if ((surface.BlendMode == 4) != additive) continue;
                var tex=surface.Texture;
                for(int t=0;t<surface.Indices.Length;t+=3)
                {
                    if((t&63)==0)token.ThrowIfCancellationRequested();
                    int i0=surface.Indices[t],i1=surface.Indices[t+1],i2=surface.Indices[t+2];
                    if(!surface.TwoSided&&Vector3.Dot(Vector3.Cross(mesh.Positions[i1]-mesh.Positions[i0],mesh.Positions[i2]-mesh.Positions[i0]),forward)<=0)continue;
                    var p0=xy[i0];var p1=xy[i1];var p2=xy[i2];
                    float den=(p1.Y-p2.Y)*(p0.X-p2.X)+(p2.X-p1.X)*(p0.Y-p2.Y);if(MathF.Abs(den)<1e-7)continue;
                    int left=Math.Clamp((int)MathF.Floor(MathF.Min(p0.X,MathF.Min(p1.X,p2.X))),0,width-1),top=Math.Clamp((int)MathF.Floor(MathF.Min(p0.Y,MathF.Min(p1.Y,p2.Y))),0,height-1);
                    int rightEdge=Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.X,MathF.Max(p1.X,p2.X))),0,width-1),bottom=Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.Y,MathF.Max(p1.Y,p2.Y))),0,height-1);
                    for(int y=top;y<=bottom;y++)for(int x=left;x<=rightEdge;x++)
                    {
                        float w0=((p1.Y-p2.Y)*(x+.5f-p2.X)+(p2.X-p1.X)*(y+.5f-p2.Y))/den;
                        float w1=((p2.Y-p0.Y)*(x+.5f-p2.X)+(p0.X-p2.X)*(y+.5f-p2.Y))/den,w2=1-w0-w1;
                        if(w0<0||w1<0||w2<0)continue;
                        float zz=w0*z[i0]+w1*z[i1]+w2*z[i2];int pixel=y*width+x;if(zz<depth[pixel]-1e-5f)continue;
                        var uv=mesh.Uvs[i0]*w0+mesh.Uvs[i1]*w1+mesh.Uvs[i2]*w2;
                        if(!float.IsFinite(uv.X)||!float.IsFinite(uv.Y))continue;
                        float u=surface.ClampU?Math.Clamp(uv.X,0,1):uv.X-MathF.Floor(uv.X),v=surface.ClampV?Math.Clamp(uv.Y,0,1):uv.Y-MathF.Floor(uv.Y);
                        int tx=Math.Clamp((int)(u*tex.Width),0,tex.Width-1),ty=Math.Clamp((int)(v*tex.Height),0,tex.Height-1),source=(ty*tex.Width+tx)*4;
                        // Texture alpha is not opacity on opaque material passes.
                        if(!surface.PassesAlphaTest(tex.Pixels[source+3]))continue;
                        var l=surface.Unlit?Vector3.One:w0*lighting[i0]+w1*lighting[i1]+w2*lighting[i2];
                        for(int k=0;k<3;k++)
                        {
                            float channel=tex.Pixels[source+k]*l[k];
                            if(additive) channel=output[pixel*4+k]+channel*tex.Pixels[source+3]/255f;
                            output[pixel*4+k]=(byte)Math.Clamp((int)channel,0,255);
                        }
                        // Additive eye/glow geometry does not punch holes or occlude later surfaces.
                        if(!additive) output[pixel*4+3]=255;
                        else output[pixel*4+3]=Math.Max(output[pixel*4+3],tex.Pixels[source+3]);
                        if(surface.DepthWrite)depth[pixel]=zz;
                    }
                }
            }
        }
        token.ThrowIfCancellationRequested();
        using var bitmap=new SKBitmap(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Unpremul));
        Marshal.Copy(output,0,bitmap.GetPixels(),output.Length);
        using var image=SKImage.FromBitmap(bitmap);using var png=image.Encode(SKEncodedImageFormat.Png,100);return png.ToArray();
    }
}
