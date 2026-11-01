#ifndef MYSTIC_FLATTEN_INCLUDED
#define MYSTIC_FLATTEN_INCLUDED

// "2D mode": squashes the avatar's depth toward a plane that faces the camera, so it reads as flat/anime-like.
// Same idea as Blender's Flattice add-on, but computed live every frame against Mystic's camera.
// All values are globals set from C# (Mystic.LookController); 0 flatten = the original MToon behaviour.

float  _MysticFlatten;          // 0 = off .. 1 = as flat as allowed
float  _MysticFlattenMinDepth;  // fraction of depth kept at full flatten, so stacked layers (eyes on face,
                                // hair over face) don't z-fight
float3 _MysticFlattenPivot;     // world-space point the depth is squashed toward (e.g. the head)
float  _MysticFlattenRadius;    // full effect within this distance of the pivot (metres)
float  _MysticFlattenFalloff;   // fades to no effect over this extra distance
float3 _MysticCameraFwd;        // the main camera's forward. Not UNITY_MATRIX_V: in the shadow pass that is the
                                // light's view, and the flattening must match what the camera sees.

float3 MysticFlatten(float3 positionWS)
{
    if (_MysticFlatten <= 0.0) return positionWS;

    float dist = distance(positionWS, _MysticFlattenPivot);
    float weight = 1.0 - smoothstep(_MysticFlattenRadius, _MysticFlattenRadius + max(_MysticFlattenFalloff, 1e-4), dist);
    float keep = lerp(1.0, _MysticFlattenMinDepth, saturate(_MysticFlatten) * weight);

    float depth = dot(positionWS - _MysticFlattenPivot, _MysticCameraFwd);
    return positionWS - _MysticCameraFwd * depth * (1.0 - keep);
}

#endif
