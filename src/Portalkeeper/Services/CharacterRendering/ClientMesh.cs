using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Portalkeeper.Services.CharacterRendering;

internal sealed class ClientMesh
{
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Vector2[] Uvs { get; }
    public List<Surface> Surfaces { get; } = new();
    public Dictionary<int,Matrix4x4> Attachments { get; } = new();
    public sealed record Surface(int[] Indices,ClientTexture Texture,bool TwoSided,bool ClampU,bool ClampV,int BlendMode,bool Unlit,bool DepthWrite)
    {
        public bool PassesAlphaTest(byte alpha) => BlendMode != 1 || alpha >= 128;
    }
    private static (int N,int Offset) ArrayBlock(byte[] b,int at,int stride)
    {
        if(at<0||at+8>b.Length)throw new InvalidDataException("Model array header.");
        uint n=BitConverter.ToUInt32(b,at),offset=BitConverter.ToUInt32(b,at+4);
        if(n>1000000||(long)offset+n*(long)stride>b.Length)throw new InvalidDataException("Model array bounds.");
        return ((int)n,(int)offset);
    }
    private static Vector3 V3(byte[] b,int p)=>new(BitConverter.ToSingle(b,p),BitConverter.ToSingle(b,p+4),BitConverter.ToSingle(b,p+8));
    public ClientMesh(ClientAssets assets,string path,Dictionary<int,ClientTexture> replacements,Func<uint,bool>? visible,CancellationToken token,Matrix4x4? transform=null)
    {
        byte[] b=assets.Read(path),skin=assets.Read(path[..^3]+"00.skin");
        if(b.Length<248 || Encoding.ASCII.GetString(b,0,4)!="MD20" || BitConverter.ToUInt32(b,4)!=264 || skin.Length<48 || Encoding.ASCII.GetString(skin,0,4)!="SKIN")throw new InvalidDataException("Unsupported model format.");
        var bones=Bones(b);var(nv,ov)=ArrayBlock(b,60,48);
        Positions=new Vector3[nv];Normals=new Vector3[nv];Uvs=new Vector2[nv];
        for(int i=0;i<nv;i++)
        {
            if((i&255)==0)token.ThrowIfCancellationRequested();
            int o=ov+i*48;var position=V3(b,o);var normal=V3(b,o+20);var pv=Vector3.Zero;var pn=Vector3.Zero;float total=0;
            for(int j=0;j<4;j++)
            {
                float weight=b[o+12+j]/255f;if(weight==0)continue;int bone=b[o+16+j];
                if(bone>=bones.Length)throw new InvalidDataException("Invalid vertex bone.");
                pv+=Vector3.Transform(position,bones[bone])*weight;pn+=Vector3.TransformNormal(normal,bones[bone])*weight;total+=weight;
            }
            if(total==0){pv=position;pn=normal;}
            if(transform.HasValue){pv=Vector3.Transform(pv,transform.Value);pn=Vector3.TransformNormal(pn,transform.Value);}
            if(!float.IsFinite(pv.X)||!float.IsFinite(pv.Y)||!float.IsFinite(pv.Z))throw new InvalidDataException("Invalid vertex position.");
            Positions[i]=pv;Normals[i]=pn.LengthSquared()>1e-9?Vector3.Normalize(pn):Vector3.UnitZ;
            Uvs[i]=new(BitConverter.ToSingle(b,o+32),BitConverter.ToSingle(b,o+36));
        }
        var(na,oa)=ArrayBlock(b,240,40);
        for(int i=0;i<na;i++)
        {
            int o=oa+i*40,id=(int)BitConverter.ToUInt32(b,o),bone=(int)BitConverter.ToUInt32(b,o+4);
            if(bone>=bones.Length)throw new InvalidDataException("Attachment bone.");
            Attachments[id]=Matrix4x4.CreateTranslation(V3(b,o+8))*bones[bone];
        }
        var(ni,oi)=ArrayBlock(skin,4,2);var(nt,ot)=ArrayBlock(skin,12,2);var indices=new int[nt];
        for(int i=0;i<nt;i++)
        {
            int index=BitConverter.ToUInt16(skin,ot+i*2);if(index>=ni)throw new InvalidDataException("Skin index.");
            indices[i]=BitConverter.ToUInt16(skin,oi+index*2);if(indices[i]>=nv)throw new InvalidDataException("Vertex index.");
        }
        var(ng,og)=ArrayBlock(skin,28,48);var(nb,ob)=ArrayBlock(skin,36,24);var(nx,ox)=ArrayBlock(b,80,16);
        var(nl,ol)=ArrayBlock(b,128,2);var(nf,of)=ArrayBlock(b,112,4);
        var loaded=new Dictionary<int,ClientTexture>();var used=new HashSet<int>();
        // Preserve the original base surface even if a glow batch precedes it in the file.
        foreach(bool glow in new[]{false,true})
        for(int i=0;i<nb;i++)
        {
            token.ThrowIfCancellationRequested();int off=ob+i*24;
            int sub=BitConverter.ToUInt16(skin,off+4),rf=BitConverter.ToUInt16(skin,off+10),lookup=BitConverter.ToUInt16(skin,off+16);
            if(sub>=ng||rf>=nf||lookup>=nl)throw new InvalidDataException("Material lookup.");
            uint gid=BitConverter.ToUInt32(skin,og+sub*48);
            if((visible is not null&&!visible(gid))||used.Contains(sub))continue;
            int flags=BitConverter.ToUInt16(b,of+rf*4),blend=BitConverter.ToUInt16(b,of+rf*4+2);
            // Keep independent static emissive geometry (such as helmet eyes).
            // Environment-map and layered reflection shaders remain outside this rasterizer.
            if(blend is not (0 or 1 or 4) || (blend==4)!=glow)continue;
            int textureId=BitConverter.ToUInt16(b,ol+lookup*2);if(textureId>=nx)throw new InvalidDataException("Texture lookup.");
            int texOff=ox+textureId*16;int type=(int)BitConverter.ToUInt32(b,texOff),texFlags=(int)BitConverter.ToUInt32(b,texOff+4);
            if(!loaded.TryGetValue(textureId,out var texture))
            {
                if(type!=0)
                {
                    if(!replacements.TryGetValue(type,out texture))throw new InvalidDataException("Character texture unavailable.");
                }
                else
                {
                    int length=checked((int)BitConverter.ToUInt32(b,texOff+8)),str=checked((int)BitConverter.ToUInt32(b,texOff+12));
                    if(length<1||str<0||(long)str+length>b.Length)throw new InvalidDataException("Texture path.");
                    string name=Encoding.UTF8.GetString(b,str,length).TrimEnd('\0');texture=ClientTexture.Decode(assets.Read(name));
                }
                loaded[textureId]=texture;
            }
            int start=BitConverter.ToUInt16(skin,og+sub*48+8),count=BitConverter.ToUInt16(skin,og+sub*48+10);
            if(start+count>nt||count%3!=0)throw new InvalidDataException("Surface indices.");
            Surfaces.Add(new(indices.Skip(start).Take(count).ToArray(),texture,(flags&4)!=0,(texFlags&1)==0,(texFlags&2)==0,blend,(flags&1)!=0,(flags&16)==0));used.Add(sub);
        }
        if(Surfaces.Count==0)throw new InvalidDataException("No visible model surfaces.");
    }
    private static Matrix4x4[] Bones(byte[] b)
    {
        var(n,o)=ArrayBlock(b,44,88);var(an,ao)=ArrayBlock(b,28,64);
        int animation=-1;
        for(int i=0;i<an;i++)if(BitConverter.ToUInt16(b,ao+i*64)==0&&(BitConverter.ToUInt32(b,ao+i*64+12)&0x20)!=0){animation=i;break;}
        var result=new Matrix4x4[n];var state=new byte[n];
        int Key(int offset,int stride)
        {
            if(animation<0)return -1;
            // Track array headers reside in M2; use only embedded Stand frame zero.
            var(count,start)=ArrayBlock(b,offset+12,8);
            int sequence=BitConverter.ToInt16(b,offset+2)>=0?0:animation;
            if(sequence>=count)return -1;
            var(keys,data)=ArrayBlock(b,start+sequence*8,stride);return keys==0?-1:data;
        }
        Matrix4x4 Calculate(int i)
        {
            if(i<0||i>=n||state[i]==1)throw new InvalidDataException("Bone hierarchy.");
            if(state[i]==2)return result[i];state[i]=1;
            int p=o+i*88,parent=BitConverter.ToInt16(b,p+8);
            var pivot=V3(b,p+76);int kt=Key(p+16,12),kr=Key(p+36,8),ks=Key(p+56,12);
            var t=kt<0?Vector3.Zero:V3(b,kt);var scale=ks<0?Vector3.One:V3(b,ks);var q=Quaternion.Identity;
            if(kr>=0)
            {
                float Unpack(int off){int v=BitConverter.ToInt16(b,off);return (v<0?v+32768:v-32767)/32767f;}
                q=new(Unpack(kr),Unpack(kr+2),Unpack(kr+4),Unpack(kr+6));if(q.LengthSquared()<1e-8)q=Quaternion.Identity;else q=Quaternion.Normalize(q);
            }
            var m=Matrix4x4.CreateTranslation(-pivot)*Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(q)*Matrix4x4.CreateTranslation(pivot+t);
            if(parent>=0)m*=Calculate(parent);result[i]=m;state[i]=2;return m;
        }
        for(int i=0;i<n;i++)Calculate(i);return result;
    }
}
