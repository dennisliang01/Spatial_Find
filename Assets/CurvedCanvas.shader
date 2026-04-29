// Unlit, double-sided, alpha-blended display shader for sampling a Canvas RenderTexture
// onto a curved mesh. Built so it works with both Built-in RP and URP without lighting,
// and with a transparent canvas clear so off-canvas pixels stay transparent.
Shader "SpatialFind/CurvedCanvasUnlit"
{
    Properties
    {
        _MainTex ("Render Texture", 2D) = "white" {}
        _Color   ("Tint",            Color) = (1, 1, 1, 1)
        [Toggle] _FlipY ("Flip Y (UV)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        LOD 100

        Cull   Off
        ZWrite Off
        ZTest  LEqual
        Blend  SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4    _MainTex_ST;
            float4    _Color;
            float     _FlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = TRANSFORM_TEX(v.uv, _MainTex);
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                o.uv = uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                return c;
            }
            ENDCG
        }
    }

    FallBack "Unlit/Transparent"
}
