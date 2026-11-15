// Road surface for gen_track roads (RoadBuilder.BuildGeneratedRoad). All parameters are set per
// road through a MaterialPropertyBlock (TrackMarkings.Apply).
//
// UVs (from RoadBuilder): U = 0.1 * metres along the road, V = 0..1 across the road.
// Edge lines always sit at the very edge of the road: left line V [0, w], right line [1 - w, 1].
// Their outer edge is the mesh edge, so the outermost line pixel and the outermost class-1 pixel
// of the segmentation mask (whole mesh, Hidden/SegReplacement) are the same pixel.
//
// _LinesOnly = 1 (carpet tracks): only the tape lines are drawn, every other pixel is discarded so
// the carpet floor underneath shows through (road and floor are the same carpet, seamless).
// The segmentation replacement shader does not clip, so the mask still covers the whole road.
//
// _MainTex is unused; it is declared so _MainTex_ST stays identity for the segmentation shader.
Shader "Custom/ProceduralRoad"
{
    Properties
    {
        _MainTex ("Unused (keep identity)", 2D) = "white" {}
        _BaseTex ("Base surface", 2D) = "gray" {}
        _BaseWin ("Base: vMin, vMax (line-free window, 0..1 = whole texture), tile U (m), tile V (m)", Vector) = (0, 1, 8, 4)
        _Tint ("Base tint (linear multiplier)", Vector) = (1, 1, 1, 1)
        _RoadWidth ("Road width (m)", Float) = 8
        _Edge ("Edge lines: on, -, width (V), -", Vector) = (1, 0, 0.03, 0)
        _EdgeColor ("Edge colour", Color) = (1, 1, 1, 1)
        _Centre ("Centre line: mode (0 none, 1 solid, 2 dashed), width (V), dash (m), gap (m)", Vector) = (2, 0.02, 3, 3)
        _CentreColor ("Centre colour", Color) = (1, 1, 1, 1)
        _Wear ("Line wear 0..1", Float) = 0.2
        _LinesOnly ("Draw only the lines (carpet)", Float) = 0
        _Glossiness ("Smoothness", Range(0, 1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;   // unused (see header)
        sampler2D _BaseTex;
        float4 _BaseWin, _Edge, _Centre, _Tint;
        fixed4 _EdgeColor, _CentreColor;
        float _RoadWidth, _Wear, _LinesOnly;
        half _Glossiness;

        struct Input { float2 uv_MainTex; };

        float hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float u = IN.uv_MainTex.x * 10.0;   // metres along the road
            float v = IN.uv_MainTex.y;          // 0..1 across the road

            // base: whole texture -> plain repeat; line-free window -> mirror-tiled across
            float tv = v * _RoadWidth / _BaseWin.w;
            bool whole = _BaseWin.x <= 0.0 && _BaseWin.y >= 1.0;
            float m = whole ? frac(tv) : 1.0 - abs(1.0 - 2.0 * frac(tv * 0.5));
            float2 buv = float2(u / _BaseWin.z, lerp(_BaseWin.x, _BaseWin.y, m));
            float2 cont = float2(u / _BaseWin.z, tv * (_BaseWin.y - _BaseWin.x));
            fixed3 col = tex2Dgrad(_BaseTex, buv, ddx(cont), ddy(cont)).rgb * _Tint.rgb;

            float wear = 1.0 - _Wear * hash21(floor(float2(u, v * _RoadWidth) * 8.0));

            // edge lines, flush with the road edge
            float ew = _Edge.z;
            bool edge = _Edge.x > 0.5 && (v < ew || v > 1.0 - ew);
            col = lerp(col, _EdgeColor.rgb, edge ? wear : 0.0);

            // centre line
            float period = _Centre.z + _Centre.w;
            bool dashOn = _Centre.x < 1.5 || frac(u / period) < _Centre.z / period;
            bool centre = _Centre.x > 0.5 && abs(v - 0.5) < 0.5 * _Centre.y && dashOn;
            col = lerp(col, _CentreColor.rgb, centre ? wear : 0.0);

            if (_LinesOnly > 0.5)
                clip((edge || centre) ? 1.0 : -1.0);

            o.Albedo = col;
            o.Metallic = 0.0;
            o.Smoothness = _Glossiness;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
