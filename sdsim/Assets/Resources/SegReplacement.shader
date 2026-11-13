// Replacement shader used by SegmentationSensor (Camera.SetReplacementShader(shader, "RenderType")).
//
// Every SubShader outputs the per-renderer class id stored in _SegId.r (= classId / 255).
// _SegId.g = 1 means "ignore alpha": the TransparentCutout pass then draws the whole mesh
// (used for RoadBuilder roads whose material only shows painted lines, see SegLabel.ignoreAlpha).
// _SegBand = (vMin, vMax, 0, 0): drivable band across the road in TEXTURE space (frac of the
// _MainTex-transformed V coordinate). Pixels of a labelled object outside the band get class 0,
// e.g. grass/kerbs painted at the sides of a road texture. vMin <= 0 / vMax >= 1 disable a side.
// _SegId is deliberately NOT declared in a Properties block: that way the value comes either
// from a MaterialPropertyBlock set by SegLabel, or from the global default (0 = background)
// set with Shader.SetGlobalVector("_SegId", Vector4.zero).
//
// Objects whose RenderType tag is not listed here (e.g. "Transparent", "Background") are
// NOT drawn into the mask, i.e. they are see-through in the segmentation image.
//
// The Tree*/Grass* SubShaders mirror Unity's built-in Internal-DepthNormalsTexture
// replacement shader so that terrain trees / grass get the same vertex animation and
// billboarding as in the RGB image (otherwise road would "show through" foliage).
// No lighting, no fog, no colour-space conversion.

Shader "Hidden/SegReplacement"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _Cutoff ("", Float) = 0.5
        _Color ("", Color) = (1,1,1,1)
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    float4 _SegId;
    // _MainTex/_Cutoff/_Color are declared per pass (after the Unity includes) because
    // some Unity tree/terrain include files already declare a subset of them.

    struct v2f_seg
    {
        float4 pos : SV_POSITION;
    };

    struct v2f_seg_uv
    {
        float4 pos : SV_POSITION;
        float2 uv  : TEXCOORD0;
    };

    struct v2f_seg_uv_col
    {
        float4 pos   : SV_POSITION;
        float2 uv    : TEXCOORD0;
        fixed4 color : COLOR;
    };

    float4 _SegBand;

    float4 segOut()
    {
        return float4(_SegId.r, 0, 0, 1);
    }

    // uvT = TRANSFORM_TEX(texcoord, _MainTex), i.e. the coordinate the RGB shader samples with.
    float4 segOutBanded(float2 uvT)
    {
        float t = frac(uvT.y);
        bool inBand = (_SegBand.x <= 0.0 || t >= _SegBand.x) && (_SegBand.y >= 1.0 || t <= _SegBand.y);
        return float4(inBand ? _SegId.r : 0.0, 0, 0, 1);
    }
    ENDCG

    // ---------------------------------------------------------------- Opaque
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 _MainTex_ST;
            v2f_seg_uv vert(appdata_base v)
            {
                v2f_seg_uv o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }
            float4 frag(v2f_seg_uv i) : SV_Target { return segOutBanded(i.uv); }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- TransparentCutout
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" }
        Pass
        {
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed _Cutoff;
            fixed4 _Color;
            v2f_seg_uv vert(appdata_base v)
            {
                v2f_seg_uv o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }
            float4 frag(v2f_seg_uv i) : SV_Target
            {
                // Same test as the Standard shader in Cutout mode: albedo alpha * _Color.a
                fixed a = tex2D(_MainTex, i.uv).a * _Color.a;
                if (_SegId.g < 0.5)
                    clip(a - _Cutoff);
                return segOutBanded(i.uv);
            }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- TreeBark (Tree Creator)
    SubShader
    {
        Tags { "RenderType"="TreeBark" }
        Pass
        {
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Lighting.cginc"
            #include "UnityBuiltin3xTreeLibrary.cginc"
            v2f_seg vert(appdata_full v)
            {
                v2f_seg o;
                TreeVertBark(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            float4 frag(v2f_seg i) : SV_Target { return segOut(); }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- TreeLeaf (Tree Creator)
    SubShader
    {
        Tags { "RenderType"="TreeLeaf" }
        Pass
        {
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Lighting.cginc"
            #include "UnityBuiltin3xTreeLibrary.cginc"
            sampler2D _MainTex;
            fixed _Cutoff;
            v2f_seg_uv vert(appdata_full v)
            {
                v2f_seg_uv o;
                TreeVertLeaf(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }
            float4 frag(v2f_seg_uv i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a - _Cutoff);
                return segOut();
            }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- TreeOpaque (terrain soft-occlusion trees)
    SubShader
    {
        Tags { "RenderType"="TreeOpaque" "DisableBatching"="True" }
        Pass
        {
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "TerrainEngine.cginc"
            v2f_seg vert(appdata_full v)
            {
                v2f_seg o;
                TerrainAnimateTree(v.vertex, v.color.w);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            float4 frag(v2f_seg i) : SV_Target { return segOut(); }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- TreeTransparentCutout
    SubShader
    {
        Tags { "RenderType"="TreeTransparentCutout" "DisableBatching"="True" }
        Pass
        {
            Cull Off
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "TerrainEngine.cginc"
            sampler2D _MainTex;
            fixed _Cutoff;
            v2f_seg_uv vert(appdata_full v)
            {
                v2f_seg_uv o;
                TerrainAnimateTree(v.vertex, v.color.w);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }
            float4 frag(v2f_seg_uv i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a - _Cutoff);
                return segOut();
            }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- Terrain detail grass (mesh)
    SubShader
    {
        Tags { "RenderType"="Grass" }
        Pass
        {
            Cull Off
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "TerrainEngine.cginc"
            sampler2D _MainTex;
            fixed _Cutoff;
            v2f_seg_uv_col vert(appdata_full v)
            {
                v2f_seg_uv_col o;
                WavingGrassVert(v);
                o.color = v.color;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }
            float4 frag(v2f_seg_uv_col i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a * i.color.a - _Cutoff);
                return segOut();
            }
            ENDCG
        }
    }

    // ---------------------------------------------------------------- Terrain detail grass (billboard)
    SubShader
    {
        Tags { "RenderType"="GrassBillboard" }
        Pass
        {
            Cull Off
            Fog { Mode Off }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "TerrainEngine.cginc"
            sampler2D _MainTex;
            fixed _Cutoff;
            v2f_seg_uv_col vert(appdata_full v)
            {
                v2f_seg_uv_col o;
                WavingGrassBillboardVert(v);
                o.color = v.color;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }
            float4 frag(v2f_seg_uv_col i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a * i.color.a - _Cutoff);
                return segOut();
            }
            ENDCG
        }
    }

    Fallback Off
}
