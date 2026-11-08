Shader "Zincat/PasswordLock/HUD Dots"
{
    Properties
    {
        _Visible ("Visible", Float) = 0
        _Progress ("Entered digits", Float) = 0
        _Length ("Password length", Range(1,20)) = 4
        _Vertical ("Screen height", Range(0.1,0.8)) = 0.34
        _Radius ("Dot radius", Range(0.004,0.015)) = 0.006
        _FilledColor ("Filled", Color) = (1,1,1,0.95)
        _EmptyColor ("Empty", Color) = (1,1,1,0.55)
        [HideInInspector] _Preview ("Editor preview", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Overlay+100" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" "VRCFallback"="Hidden" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON UNITY_SINGLE_PASS_STEREO
            #include "UnityCG.cginc"
            float _Visible, _Progress, _Length, _Vertical, _Radius, _Preview;
            float _VRChatCameraMode, _VRChatMirrorMode;
            float4 _FilledColor, _EmptyColor;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float allowed : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // Both eyes project the SAME head-relative plane, 1.5 metres away.
                // Equal clip-space coordinates do not align in asymmetric HMD frusta.
                const float depth = 1.5;
                float3 right, up, back, eyeOffset;
                float projectionX, projectionY;
                #if defined(USING_STEREO_MATRICES)
                    float4x4 left = unity_StereoMatrixInvV[0];
                    float4x4 rightEye = unity_StereoMatrixInvV[1];
                    right = normalize(left._m00_m10_m20 + rightEye._m00_m10_m20);
                    up = normalize(left._m01_m11_m21 + rightEye._m01_m11_m21);
                    // Unity view space looks down -Z (inverse-view basis is reflected).
                    back = -normalize(cross(right, up));
                    up = normalize(cross(right, back));
                    float3 head = (left._m03_m13_m23 + rightEye._m03_m13_m23) * 0.5;
                    eyeOffset = head - UNITY_MATRIX_I_V._m03_m13_m23;
                    projectionX = (abs(unity_StereoMatrixP[0]._m00) + abs(unity_StereoMatrixP[1]._m00)) * 0.5;
                    projectionY = (abs(unity_StereoMatrixP[0]._m11) + abs(unity_StereoMatrixP[1]._m11)) * 0.5;
                #else
                    right = UNITY_MATRIX_I_V._m00_m10_m20;
                    up = UNITY_MATRIX_I_V._m01_m11_m21;
                    back = UNITY_MATRIX_I_V._m02_m12_m22;
                    eyeOffset = 0;
                    projectionX = abs(UNITY_MATRIX_P._m00);
                    projectionY = abs(UNITY_MATRIX_P._m11);
                #endif
                float aspect = projectionY / max(projectionX, 0.001);
                float pitch = _Radius * 3.2;
                float2 panel = float2(pitch * 10, pitch * 2);
                float2 screen = float2(0.5, _Vertical) + (v.uv - 0.5) * panel / float2(aspect, 1);
                float2 plane = (screen * 2 - 1) * depth / max(projectionY, 0.001) * float2(aspect, 1);
                float3 offset = eyeOffset + right * plane.x + up * plane.y - back * depth;
                float3 view = mul((float3x3)UNITY_MATRIX_V, offset);
                // UNITY_MATRIX_P already includes render-target Y inversion.
                o.vertex = mul(UNITY_MATRIX_P, float4(view, 1));
                o.uv = v.uv;
                o.allowed = _VRChatCameraMode < 0.5 && _VRChatMirrorMode < 0.5;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(min(_Visible, i.allowed) - 0.5);
                float length = clamp(floor(_Length + 0.5), 1, 20);
                float pitch = _Radius * 3.2;
                float2 p = (i.uv - 0.5) * float2(pitch * 10, pitch * 2);
                float rows = length > 10 ? 2 : 1;
                float row = rows > 1 ? step(i.uv.y, 0.5) : 0;
                float count = min(10, length - row * 10);
                float column = floor(p.x / pitch + (count - 1) * 0.5 + 0.5);
                clip(min(column + 0.5, count - column - 0.5));
                float2 center = float2((column - (count - 1) * 0.5) * pitch, rows > 1 ? (0.5 - row) * pitch : 0);
                float d = distance(p, center) / _Radius;
                float aa = max(fwidth(d), 0.015);
                float outer = 1 - smoothstep(1 - aa, 1 + aa, d);
                float filled = step(row * 10 + column + 0.5, _Progress);
                float ring = smoothstep(0.65 - aa, 0.65 + aa, d);
                fixed4 color = lerp(_EmptyColor, _FilledColor, filled);
                float whiteAlpha = color.a * outer * lerp(ring, 1, filled);
                float shadow = (1 - smoothstep(1.2 - aa, 1.2 + aa, d)) * lerp(smoothstep(0.45 - aa, 0.45 + aa, d), 1, filled) * 0.65;
                color.a = whiteAlpha + shadow * (1 - whiteAlpha);
                color.rgb *= whiteAlpha / max(color.a, 0.001);
                clip(color.a - 0.001);
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
