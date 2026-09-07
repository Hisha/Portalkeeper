using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using Portalkeeper.Models;

namespace Portalkeeper.Services.CharacterRendering;

internal sealed class CharacterScene
{
    public string Model { get; private set; } = "";
    public Dictionary<int,ClientTexture> Textures { get; } = new();
    public List<(string Model,string Texture,int Point)> Attachments { get; } = new();
    private readonly Dictionary<int,uint> _geosets = Enumerable.Range(1,18).ToDictionary(x=>x,x=>1u);
    private uint _hair;
    private uint[]? _helmet;
    private int _race;
    public bool Visible(uint id)
    {
        if(id==0)return true;
        int group=(int)id/100;
        int mask=group switch {0=>1,1=>2,2=>3,3=>4,7=>5,_=>0};
        if(_helmet is not null && mask>0 && (_helmet[mask] & (1u<<_race))!=0)return false;
        return group==0 ? id==_hair : id==group*100+_geosets.GetValueOrDefault(group,0u);
    }
    public static CharacterScene Resolve(ClientAssets assets,ArmoryCharacter c,CancellationToken token)
    {
        if(!new[]{1,2,3,4,5,6,7,8,10,11}.Contains(c.Race) || c.Gender is <0 or >1) throw new NotSupportedException("This character race is unavailable in the 3.3.5 client.");
        var s=new CharacterScene {_race=c.Race};var a=c.Appearance;
        var races=new ClientDbc(assets,"ChrRaces",69);var displays=new ClientDbc(assets,"CreatureDisplayInfo",16);
        var models=new ClientDbc(assets,"CreatureModelData",28);var sections=new ClientDbc(assets,"CharSections",10);
        var items=new ClientDbc(assets,"ItemDisplayInfo",25);var hairs=new ClientDbc(assets,"CharHairGeosets",6);
        var facial=new ClientDbc(assets,"CharacterFacialHairStyles",8);var helmets=new ClientDbc(assets,"HelmetGeosetVisData",8);
        var rr=races.Id((uint)c.Race);
        s.Model=Path.ChangeExtension(models.Text(models.Id(displays.Id(rr[4+c.Gender])[1])[2]),"m2");
        var atlas=new ClientTexture(512,512);s.Textures[1]=atlas;
        var regions=new (int X,int Y,int W,int H)[]{(0,0,256,256),(0,0,128,64),(0,64,128,64),(0,128,128,32),(0,160,128,32),(0,192,128,64),(128,0,128,64),(128,64,128,32),(128,96,128,64),(128,160,128,64),(128,224,128,32)};
        ClientTexture Texture(string name)=>ClientTexture.Decode(assets.Read(name));
        void Layer(int region,string name)
        {
            if(string.IsNullOrEmpty(name))return;
            token.ThrowIfCancellationRequested();var r=regions[region];atlas.Composite(Texture(name),r.X*2,r.Y*2,r.W*2,r.H*2);
        }
        uint[]? Section(int type,int variation,int color)=>sections.Rows
            .Where(r=>r[1]==c.Race && r[2]==c.Gender && r[3]==type && r[8]==variation && r[9]==color && (r[7]&1)!=0)
            // Prefer the usual class variant, but accept an exact exported appearance
            // when the installed client only supplies the other variant. Stable order
            // preserves the original first-record choice within each preference.
            .OrderByDescending(r=>((r[7]&4)!=0)==(c.Class==6)).FirstOrDefault();
        var skin=Section(0,0,a.Skin)??throw new InvalidDataException("Skin appearance is not present in the local client.");
        Layer(0,sections.Text(skin[4]));
        if(skin[5]!=0)s.Textures[8]=Texture(sections.Text(skin[5])); // race fur texture
        var underwear=Section(4,0,a.Skin);
        if(underwear is not null){Layer(8,sections.Text(underwear[4]));Layer(6,sections.Text(underwear[5]));}
        var face=Section(1,a.Face,a.Skin)??throw new InvalidDataException("Face appearance is not present in the local client.");
        Layer(5,sections.Text(face[4]));Layer(4,sections.Text(face[5]));
        var beard=Section(2,a.FacialStyle,a.HairColor);
        void OptionalLayer(int region,uint offset) { var name=sections.Text(offset); if(name.Length>0 && assets.Exists(name))Layer(region,name); }
        // Stock section rows can contain nonexistent optional beard/scalp overlays.
        if(beard is not null){OptionalLayer(5,beard[4]);OptionalLayer(4,beard[5]);}
        var hair=Section(3,a.HairStyle,a.HairColor)??Section(3,0,a.HairColor);
        if(hair is not null)
        {
            // Some stock Tauren hair rows name absent textures that the model never uses.
            if(hair[4]!=0 && assets.Exists(sections.Text(hair[4])))s.Textures[6]=Texture(sections.Text(hair[4]));
            OptionalLayer(5,hair[5]);OptionalLayer(4,hair[6]);
        }
        s._hair=hairs.Rows.FirstOrDefault(r=>r[1]==c.Race && r[2]==c.Gender && r[3]==a.HairStyle)?[4]??0;
        var f=facial.Rows.FirstOrDefault(r=>r[0]==c.Race && r[1]==c.Gender && r[2]==a.FacialStyle);
        s._geosets[1]=f?[3]??0;s._geosets[2]=f?[5]??0;s._geosets[3]=f?[4]??0;s._geosets[7]=2;s._geosets[12]=0;s._geosets[15]=0;
        s._geosets[17]=c.Class==6?3u:1u;
        var equipped=c.Equipment.GroupBy(e=>e.Slot).ToDictionary(g=>g.Key,g=>g.First());
        bool robe=equipped.Values.Any(e=>e.Slot is 4 or 6 && (e.InventoryType==20 || items.Id((uint)e.DisplayId)[9]==1));
        int[] order=robe?new[]{3,0,1,2,7,6,8,4,9,18,5,15,16,14,17}:new[]{3,0,1,2,6,7,4,18,5,8,9,15,16,14,17};
        string sex=c.Gender==0?"M":"F";
        string[] dirs={"ArmUpper","ArmLower","Hand","TorsoUpper","TorsoLower","LegUpper","LegLower","Foot"};
        int[] regionMap={1,2,3,6,7,8,9,10};
        foreach(int slot in order)
        {
            token.ThrowIfCancellationRequested();if(!equipped.TryGetValue(slot,out var e))continue;
            var r=items.Id((uint)e.DisplayId);
            if(slot==4)s._geosets[8]=1+r[7];if(slot==6)s._geosets[9]=1+r[8];if(slot==7)s._geosets[5]=1+r[7];if(slot==9)s._geosets[4]=1+r[7];if(slot==18)s._geosets[12]=2;
            // Guild-tabard emblems are not in schema v1; retain the armor rather than invent an emblem.
            bool guildTabard=e.Entry is 5976 or 69209 or 69210;
            int[] components=slot switch {3=>new[]{0,1,3,4},4=>robe?new[]{0,1,3,4,5,6}:new[]{0,1,3,4},5=>new[]{4,5},6=>new[]{5,6},7=>(rr[1]&2)!=0?new[]{6}:new[]{6,7},8=>new[]{1},9=>new[]{1,2},18 when !guildTabard=>new[]{3,4},_=>Array.Empty<int>()};
            if(guildTabard)s._geosets[12]=0;
            foreach(int k in components)
            {
                string name=items.Text(r[15+k]);if(name.Length==0)continue;
                string prefix=@"Item\TextureComponents\"+dirs[k]+@"Texture\"+name;
                string[] suffixes=name.Length>=2 && char.ToUpperInvariant(name[^2])=='L'?new[]{"_U","_"+sex,""}:new[]{"_"+sex,"_U",""};
                string path=suffixes.Select(x=>prefix+x+".blp").FirstOrDefault(assets.Exists)??throw new FileNotFoundException("Equipment texture unavailable.");
                Layer(regionMap[k],path);
            }
            if(slot is 0 or 2 or 15 or 16 || (slot==17 && e.ItemClass==2))
            {
                string folder=slot switch {0=>"Head",2=>"Shoulder",16 when e.InventoryType==14=>"Shield",_=>"Weapon"};
                // Do not replace an equipped main-hand weapon with a ranged weapon.
                if(slot==17 && equipped.ContainsKey(15))continue;
                int[] points=slot switch {0=>new[]{11},2=>new[]{6,5},15 or 17=>new[]{1},_=>new[]{folder=="Shield"?0:2}};
                for(int j=0;j<points.Length;j++)
                {
                    string model=items.Text(r[1+j]);if(model.Length==0)continue;
                    string stem=Path.GetFileNameWithoutExtension(model);
                    if(slot==0)stem+="_"+races.Text(rr[6])+sex;
                    string modelPath=@"Item\ObjectComponents\"+folder+@"\"+stem+".m2";
                    string texture=items.Text(r[3+j]);
                    s.Attachments.Add((modelPath,texture.Length==0?"":@"Item\ObjectComponents\"+folder+@"\"+texture+".blp",points[j]));
                }
                if(slot==0 && r[13+c.Gender]!=0)s._helmet=helmets.Id(r[13+c.Gender]);
            }
            if(slot==14)
            {
                string name=items.Text(r[3]);if(name.Length>0)s.Textures[2]=Texture(@"Item\ObjectComponents\Cape\"+name+".blp");
                s._geosets[15]=1+r[7];
            }
        }
        if(s._geosets[4]>1)s._geosets[8]=0;
        if(robe){s._geosets[13]=2;s._geosets[5]=0;s._geosets[12]=0;}
        return s;
    }
}
