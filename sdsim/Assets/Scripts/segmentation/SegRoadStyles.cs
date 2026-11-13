using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drivable band of each road texture used on RoadBuilder roads.
///
/// RoadBuilder maps texture V across the road (V=0 one edge, V=1 the other) and U along it.
/// Several textures paint grass, dirt, kerbs or sidewalks at their sides; those rows must be
/// background (class 0) in the mask, so each texture gets the V range that is real road
/// surface (asphalt, dirt road, cobblestone, sand ... including painted lines).
///
/// Values were measured on the texture images (row of the road/shoulder transition;
/// V = 1 - row / height because Unity's V runs bottom-up).
///
/// Lookup is by Texture.name (asset file name without extension). Unknown textures label the
/// whole road mesh (with a one-time warning), which is correct for textures that are road
/// from edge to edge.
/// </summary>
public static class SegRoadStyles
{
    static readonly Dictionary<string, Vector2> Bands = new Dictionary<string, Vector2>
    {
        // asphalt, yellow centre line, white edge lines; dirt/grass strip at image bottom (94.5 %..100 %)
        { "TexturesCom_Roads0086_1_seamless_S",   new Vector2(0.055f, 1f) },
        { "TexturesCom_Roads0086_1_seamless_S 1", new Vector2(0.055f, 1f) },
        // same base asphalt, white dashed centre line
        { "UdacityChallengeRoad",                 new Vector2(0.055f, 1f) },
        // black road, dashed centre line, white edges: road edge to edge
        { "TransparentRoad",                      new Vector2(0f, 1f) },
        // asphalt without centre line; kerbs + dirt outside rows 22.5 %..77.6 %
        { "UdacitySimRoad",                       new Vector2(0.224f, 0.775f) },
        // dirt road; grass/stones outside rows 35.5 %..78.5 %
        { "TexturesCom_Roads0075_1_seamless_S",   new Vector2(0.215f, 0.645f) },
        // cobblestone; black gap + sidewalk outside rows 24.3 %..76.7 %
        { "UdacityRoadBridge",                    new Vector2(0.233f, 0.757f) },
        // plain sand, edge to edge
        { "SandAlbedo",                           new Vector2(0f, 1f) },
    };

    static readonly HashSet<string> warned = new HashSet<string>();

    public static Vector2 GetDrivableBand(Texture tex)
    {
        if (tex == null)
            return SegLabel.FullBand;

        Vector2 band;
        if (Bands.TryGetValue(tex.name, out band))
            return band;

        if (warned.Add(tex.name))
            Debug.LogWarning("SegRoadStyles: no drivable band registered for road texture '" + tex.name +
                             "'; the whole road mesh is labelled road. Add it to SegRoadStyles.Bands if " +
                             "the texture has grass/kerbs painted at its sides.");
        return SegLabel.FullBand;
    }
}
