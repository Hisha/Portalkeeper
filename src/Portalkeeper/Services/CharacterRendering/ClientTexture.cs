using System;
using System.IO;

namespace Portalkeeper.Services.CharacterRendering;

internal sealed class ClientTexture
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; } // RGBA
    public ClientTexture(int width,int height) { Width=width;Height=height;Pixels=new byte[checked(width*height*4)]; }
    public static ClientTexture Decode(byte[] b)
    {
        if(b.Length<148 || b[0]!='B' || b[1]!='L' || b[2]!='P' || b[3]!='2' || BitConverter.ToUInt32(b,4)!=1) throw new InvalidDataException("BLP2 texture required.");
        int w=checked((int)BitConverter.ToUInt32(b,12)),h=checked((int)BitConverter.ToUInt32(b,16));
        if(w<1 || h<1 || (long)w*h>16777216) throw new InvalidDataException("Texture dimensions.");
        int start=checked((int)BitConverter.ToUInt32(b,20)),length=checked((int)BitConverter.ToUInt32(b,84));
        if(start<148 || length<0 || (long)start+length>b.Length) throw new InvalidDataException("Texture bounds.");
        var result=new ClientTexture(w,h);var p=result.Pixels;
        int encoding=b[8],depth=b[9],alphaEncoding=b[10],n=w*h;
        if(encoding==1)
        {
            if(b.Length<1172 || depth is not (0 or 1 or 4 or 8) || length<n+(n*depth+7)/8) throw new InvalidDataException("Palette alpha bounds.");
            for(int i=0;i<n;i++)
            {
                int c=148+b[start+i]*4;
                p[i*4]=b[c+2];p[i*4+1]=b[c+1];p[i*4+2]=b[c];
                p[i*4+3]=depth==0?(byte)255:(byte)(((b[start+n+i*depth/8]>>(i*depth%8))&((1<<depth)-1))*255/((1<<depth)-1));
            }
        }
        else if(encoding==2)
        {
            if(alphaEncoding is not (0 or 1 or 7)) throw new InvalidDataException("DXT format.");
            int blockSize=alphaEncoding==0?8:16,cols=(w+3)/4,rows=(h+3)/4;
            if(length<(long)cols*rows*blockSize) throw new InvalidDataException("DXT bounds.");
            for(int by=0;by<rows;by++) for(int bx=0;bx<cols;bx++)
            {
                int block=start+(by*cols+bx)*blockSize,color=block+(blockSize==16?8:0);
                ushort c0=BitConverter.ToUInt16(b,color),c1=BitConverter.ToUInt16(b,color+2);
                var colors=new byte[16];
                void Set565(int at,ushort c) { colors[at]=(byte)(((c>>11)&31)*255/31);colors[at+1]=(byte)(((c>>5)&63)*255/63);colors[at+2]=(byte)((c&31)*255/31);colors[at+3]=255; }
                Set565(0,c0);Set565(4,c1);
                bool four=c0>c1 || alphaEncoding!=0;
                for(int k=0;k<3;k++) {colors[8+k]=(byte)(four?(2*colors[k]+colors[4+k])/3:(colors[k]+colors[4+k])/2);colors[12+k]=(byte)(four?(colors[k]+2*colors[4+k])/3:0);}
                colors[11]=255;colors[15]=four?(byte)255:(byte)0;
                uint indices=BitConverter.ToUInt32(b,color+4);
                var alphas=new byte[8];ulong alphaBits=0;
                if(alphaEncoding==7)
                {
                    alphas[0]=b[block];alphas[1]=b[block+1];
                    if(alphas[0]>alphas[1]) for(int k=1;k<=6;k++) alphas[k+1]=(byte)(((7-k)*alphas[0]+k*alphas[1])/7);
                    else {for(int k=1;k<=4;k++) alphas[k+1]=(byte)(((5-k)*alphas[0]+k*alphas[1])/5);alphas[6]=0;alphas[7]=255;}
                    for(int k=0;k<6;k++) alphaBits|=(ulong)b[block+2+k]<<(8*k);
                }
                for(int y=0;y<4;y++) for(int x=0;x<4;x++)
                {
                    int px=bx*4+x,py=by*4+y,i=y*4+x;if(px>=w||py>=h)continue;
                    int c=(int)((indices>>(i*2))&3)*4,d=(py*w+px)*4;
                    Array.Copy(colors,c,p,d,4);
                    if(alphaEncoding==1)p[d+3]=(byte)(((b[block+i/2]>>(4*(i%2)))&15)*17);
                    else if(alphaEncoding==7)p[d+3]=alphas[(int)((alphaBits>>(3*i))&7)];
                    else if(depth==0)p[d+3]=255;
                }
            }
        }
        else if(encoding==3)
        {
            if(length<n*4)throw new InvalidDataException("BGRA bounds.");
            for(int i=0;i<n;i++){p[i*4]=b[start+i*4+2];p[i*4+1]=b[start+i*4+1];p[i*4+2]=b[start+i*4];p[i*4+3]=b[start+i*4+3];}
        }
        else throw new InvalidDataException("Unsupported BLP encoding.");
        return result;
    }
    public void Composite(ClientTexture source,int x,int y,int width,int height)
    {
        for(int j=0;j<height;j++)for(int i=0;i<width;i++)
        {
            int si=((j*source.Height/height)*source.Width+i*source.Width/width)*4,d=((y+j)*Width+x+i)*4;
            int a=source.Pixels[si+3];
            for(int k=0;k<3;k++)Pixels[d+k]=(byte)((source.Pixels[si+k]*a+Pixels[d+k]*(255-a)+127)/255);
            Pixels[d+3]=(byte)(a+Pixels[d+3]*(255-a)/255);
        }
    }
}
