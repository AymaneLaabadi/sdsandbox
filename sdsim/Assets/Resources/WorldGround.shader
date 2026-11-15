// Floor for carpet tracks (gen_track, TrackGenerator.SetGround): the texture is mapped in world
// space (X/Z in metres / _Tile), so the floor is one continuous carpet regardless of the ground
// mesh's UVs. The road of a carpet track only draws its tape lines (ProceduralRoad _LinesOnly),
// so the track surface is this same floor.
Shader "Custom/WorldGround"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Tint ("Tint (linear multiplier)", Vector) = (1, 1, 1, 1)
        _Tile ("Metres per texture repeat", Float) = 2
        _Glossiness ("Smoothness", Range(0, 1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        float4 _Tint;
        float _Tile;
        half _Glossiness;

        struct Input { float3 worldPos; };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = tex2D(_MainTex, IN.worldPos.xz / _Tile).rgb * _Tint.rgb;
            o.Metallic = 0.0;
            o.Smoothness = _Glossiness;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
