using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Matériaux » (textures) : un cabinet de curiosités dans une salle de bal. Objets scannés de Poly Haven (licence
/// CC0) posés sur une table en bois de rose vernie qui les reflète, autour d'eux des socles de musée ; éclairage par un
/// vrai ciel HDR (harmoniques sphériques et environnement préfiltré GGX), lumière de la fenêtre avec ombres, bougie dans
/// la lanterne, rayons de lumière dans la poussière, profondeur de champ qui suit l'objet regardé. Chaque pixel lit
/// plusieurs textures haute définition avec filtrage anisotrope.
/// </summary>
internal sealed class MaterialsScene : BenchScene
{
    private const float TableTop = 0.76f;
    private const float TableRadius = 1.0f;
    private const int EnvironmentSize = 256;
    private const int EnvironmentMips = 7;
    private const int Stride = GltfModel.FloatsPerVertex * 4;

    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(2.2f, 1.55f, 2.0f), new Vector3(0f, 0.95f, 0f), 42f)),
        (14, new CameraPose(new Vector3(1.0f, 1.12f, 0.75f), new Vector3(0.05f, 1.0f, -0.3f), 36f)),
        (28, new CameraPose(new Vector3(0.45f, 1.06f, 0.24f), new Vector3(0.0f, 1.05f, -0.32f), 32f)),
        (42, new CameraPose(new Vector3(0.88f, 0.98f, 0.18f), new Vector3(0.42f, 0.96f, -0.22f), 34f)),
        (56, new CameraPose(new Vector3(1.05f, 0.92f, 0.95f), new Vector3(0.35f, 0.8f, 0.4f), 36f)),
        (70, new CameraPose(new Vector3(0.15f, 0.92f, 1.15f), new Vector3(-0.12f, 0.8f, 0.5f), 34f)),
        (84, new CameraPose(new Vector3(-0.88f, 0.98f, 0.78f), new Vector3(-0.55f, 0.92f, 0.2f), 34f)),
        (98, new CameraPose(new Vector3(-1.05f, 1.0f, -0.38f), new Vector3(-0.4f, 0.88f, -0.15f), 34f)),
        (120, new CameraPose(new Vector3(-2.2f, 2.3f, -1.8f), new Vector3(0f, 0.85f, 0f), 44f)));

    // Les objets sur la table : modèle, position (x, z), orientation (degrés), échelle, bascule (degrés, autour de x).
    private static readonly Placement[] Table =
    [
        new("marble_bust_01", new Vector2(0.0f, -0.32f), 20f, 1.0f),
        new("antique_ceramic_vase_01", new Vector2(0.5f, -0.3f), 0f, 1.0f),
        new("brass_vase_03", new Vector2(0.3f, 0.02f), 0f, 1.1f),
        new("horse_statue_01", new Vector2(-0.42f, -0.18f), 60f, 1.25f),
        new("Lantern_01", new Vector2(-0.6f, 0.22f), 30f, 1.1f),
        new("tea_set_01", new Vector2(0.38f, 0.44f), -25f, 0.62f),
        new("Camera_01", new Vector2(-0.22f, 0.52f), 145f, 1.0f),
        new("alarm_clock_01", new Vector2(0.06f, 0.64f), 200f, 1.15f),
        new("carved_wooden_elephant", new Vector2(-0.08f, 0.26f), 75f, 1.5f),
        new("antique_katana_01", new Vector2(0.0f, -0.66f), 8f, 1.0f, -90f),
    ];

    // Les socles de musée tout autour, chacun avec une pièce agrandie (floue au loin, par la profondeur de champ).
    private static readonly string[] PlinthModels =
    [
        "marble_bust_01", "horse_statue_01", "antique_ceramic_vase_01", "brass_vase_03", "Lantern_01", "carved_wooden_elephant", "marble_bust_01", "antique_ceramic_vase_01",
        "horse_statue_01", "brass_vase_03", "Camera_01", "alarm_clock_01", "marble_bust_01", "Lantern_01", "antique_ceramic_vase_01", "carved_wooden_elephant",
    ];

    private readonly Dictionary<string, ModelGpu> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITexture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(ModelGpu Model, Matrix4x4 World)> _objects = [];
    private readonly List<Matrix4x4> _plinths = [];
    private Vector4[] _sh = new Vector4[9];
    private Vector3 _keyDirection;
    private Vector3 _candle;
    private bool _light;
    private int _shadowSize;
    private ITexture? _sky;
    private ITexture? _environment;
    private ITexture? _brdf;
    private ITexture? _shadowMap;
    private ITexture? _reflection;
    private ITexture? _reflectionDepth;
    private ITexture? _floorReflection;
    private ITexture? _floorReflectionDepth;
    private ITexture? _focusSource;
    private ITexture? _white;
    private ITexture? _flatNormal;
    private ITexture? _defaultSurface;
    private ProceduralMesh? _tableMesh;
    private ProceduralMesh? _floorMesh;
    private ProceduralMesh? _plinthMesh;
    private IPipeline? _skyPipeline;
    private IPipeline? _objectPipeline;
    private IPipeline? _glassPipeline;
    private IPipeline? _shadowPipeline;
    private IPipeline? _dustPipeline;
    private IPipeline? _occlusionPipeline;
    private IPipeline? _flamePipeline;
    private IPipeline? _focusPipeline;
    private IPipeline? _reflectionSky;
    private IPipeline? _reflectionObjects;
    private IPipeline? _reflectionGlass;
    private IPipeline? _prefilterPipeline;
    private IPipeline? _lutPipeline;

    public override string Id => "materials";

    public override string Title => T("Cabinet de curiosités");

    public override string Subtitle => T("Matériaux : objets scannés en haute définition, reflets, lumière d'un vrai ciel HDR");

    public override GpuCapability Capability => GpuCapability.Textures;

    public override bool HasOverlay => true;

    /// <summary>Dossier des modèles, textures et ciel de Poly Haven (CC0), livrés avec MAUS.</summary>
    public static string AssetsFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "PolyHaven");

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        var shaders = context.Shaders;
        _light = context.Light;
        _shadowSize = _light ? 1536 : 2048;

        // Ciel HDR : texture du fond, éclairage diffus (harmoniques sphériques), direction de la fenêtre.
        var (sky, skyWidth, skyHeight) = ImageAssets.LoadHdr(System.IO.Path.Combine(AssetsFolder, "ballroom", "ballroom_2k.hdr"));
        _sh = ImageAssets.IrradianceSh(Downsample(sky, skyWidth, skyHeight, 8, out var smallWidth, out var smallHeight), smallWidth, smallHeight);
        var dominant = ImageAssets.DominantDirection(_sh);
        var flat = Vector3.Normalize(new Vector3(dominant.X, 0f, dominant.Z) + new Vector3(1e-4f, 0f, 0f));
        _keyDirection = Vector3.Normalize((flat * MathF.Cos(0.62f)) + (Vector3.UnitY * MathF.Sin(0.62f)));
        _sky = ImageAssets.HdrTexture(device, sky, skyWidth, skyHeight, "Salle de bal (HDR)");

        _white = OnePixel(device, 255, 255, 255, 255, PixelFormat.Rgba8UnormSrgb, "Blanc");
        _flatNormal = OnePixel(device, 128, 128, 255, 255, PixelFormat.Rgba8Unorm, "Normale plate");
        _defaultSurface = OnePixel(device, 255, 255, 255, 255, PixelFormat.Rgba8Unorm, "Surface");

        foreach (var name in Table.Select(p => p.Model).Concat(PlinthModels).Distinct())
        {
            _models[name] = LoadModel(device, name);
        }

        PlaceObjects();
        _tableMesh = ProceduralMesh.Create(device, "Table", TableGeometry());
        _floorMesh = ProceduralMesh.Create(device, "Parquet", FloorGeometry());
        _plinthMesh = ProceduralMesh.Create(device, "Socle", PlinthGeometry());
        _textures["rosewood:base"] = ImageAssets.LoadTexture(device, Asset("rosewood_veneer1", "rosewood_veneer1_diff_2k.jpg"), ImageRole.Color, _light ? 1 : 0);
        _textures["rosewood:normal"] = ImageAssets.LoadTexture(device, Asset("rosewood_veneer1", "rosewood_veneer1_nor_gl_2k.jpg"), ImageRole.Normal, _light ? 1 : 0);
        _textures["rosewood:surface"] = ImageAssets.LoadTexture(device, Asset("rosewood_veneer1", "rosewood_veneer1_arm_2k.jpg"), ImageRole.Data, _light ? 1 : 0);
        _textures["parquet:base"] = ImageAssets.LoadTexture(device, Asset("herringbone_parquet", "herringbone_parquet_diff_1k.jpg"), ImageRole.Color);
        _textures["parquet:normal"] = ImageAssets.LoadTexture(device, Asset("herringbone_parquet", "herringbone_parquet_nor_gl_1k.jpg"), ImageRole.Normal);
        _textures["parquet:surface"] = ImageAssets.LoadTexture(device, Asset("herringbone_parquet", "herringbone_parquet_arm_1k.jpg"), ImageRole.Data);
        _textures["parquet:height"] = ImageAssets.LoadTexture(device, Asset("herringbone_parquet", "herringbone_parquet_disp_1k.jpg"), ImageRole.Data);
        _textures["rosewood:height"] = ImageAssets.LoadTexture(device, Asset("rosewood_veneer1", "rosewood_veneer1_disp_2k.jpg"), ImageRole.Data, _light ? 1 : 0);

        var size = context.Size;
        _shadowMap = device.CreateTexture(TextureDesc.DepthTarget(_shadowSize, _shadowSize, "Ombres de la fenêtre"));
        _reflection = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Reflet de la table"));
        _reflectionDepth = device.CreateTexture(TextureDesc.DepthTarget(size.Width, size.Height, "Reflet de la table : profondeur"));
        _floorReflection = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Reflet du parquet"));
        _floorReflectionDepth = device.CreateTexture(TextureDesc.DepthTarget(size.Width, size.Height, "Reflet du parquet : profondeur"));
        _focusSource = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Profondeur de champ"));
        _environment = device.CreateTexture(TextureDesc.Cube(EnvironmentSize, EnvironmentMips, PixelFormat.Rgba16Float, "Environnement préfiltré"));
        _brdf = device.CreateTexture(TextureDesc.Target(128, 128, PixelFormat.Rg16Float, "Table de la BRDF"));

        var fullscreen = shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0");
        var objectVs = shaders.Get("materials.hlsl", "ObjectVS", "vs_5_0");
        VertexElement[] layout =
        [
            new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12),
            new("TANGENT", 0, VertexFormat.Float4, 24), new("TEXCOORD", 0, VertexFormat.Float2, 40),
        ];
        PixelFormat[] targets = [PixelFormat.Rgba16Float, PixelFormat.Rg16Float];
        var objectPs = shaders.Get("materials.hlsl", "ObjectPS", "ps_5_0", context.Defines);
        var skyPs = shaders.Get("materials.hlsl", "SkyPS", "ps_5_0", context.Defines);
        var glassPs = shaders.Get("materials.hlsl", "GlassPS", "ps_5_0", context.Defines);
        _skyPipeline = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : salle", fullscreen, skyPs, [], BlendMode.Opaque, DepthMode.None, CullMode.None, targets));
        _objectPipeline = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : objets", objectVs, objectPs, layout, BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, targets, PixelFormat.D32Float));
        _glassPipeline = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : verre", objectVs, glassPs, layout, BlendMode.Premultiplied, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
        _shadowPipeline = device.CreatePipeline(new GraphicsPipelineDesc(
            "Matériaux : ombres", shaders.Get("materials.hlsl", "ShadowVS", "vs_5_0"), null, layout, BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, [], PixelFormat.D32Float, DepthBias: 800, SlopeScaledDepthBias: 1.5f));
        _occlusionPipeline = device.CreatePipeline(PostProcess.Fullscreen("Matériaux : occlusion", fullscreen, shaders.Get("materials.hlsl", "OcclusionPS", "ps_5_0", context.Defines), PixelFormat.Rgba16Float, BlendMode.Multiply));
        _dustPipeline = device.CreatePipeline(PostProcess.Fullscreen("Matériaux : poussière", fullscreen, shaders.Get("materials.hlsl", "DustPS", "ps_5_0", context.Defines), PixelFormat.Rgba16Float, BlendMode.Additive));
        _flamePipeline = device.CreatePipeline(new GraphicsPipelineDesc(
            "Matériaux : flamme", shaders.Get("materials.hlsl", "FlameVS", "vs_5_0"), shaders.Get("materials.hlsl", "FlamePS", "ps_5_0"), [], BlendMode.Additive, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
        _focusPipeline = device.CreatePipeline(PostProcess.Fullscreen("Matériaux : profondeur de champ", fullscreen, shaders.Get("materials.hlsl", "FocusPS", "ps_5_0", context.Defines), PixelFormat.Rgba16Float, BlendMode.Opaque));
        _reflectionSky = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : reflet de la salle", fullscreen, skyPs, [], BlendMode.Opaque, DepthMode.None, CullMode.None, [PixelFormat.Rgba16Float]));
        _reflectionObjects = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : reflet des objets", objectVs, objectPs, layout, BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
        _reflectionGlass = device.CreatePipeline(new GraphicsPipelineDesc("Matériaux : reflet du verre", objectVs, glassPs, layout, BlendMode.Premultiplied, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));

        Precompute(context, fullscreen);
    }

    public override SceneState Evaluate(double time)
    {
        var camera = Path.Evaluate(PathTime(time, Path));
        var focus = Vector3.Distance(camera.Position, camera.Target);
        return new SceneState(camera, new ColorGrade
        {
            Exposure = 0.82f,
            BloomIntensity = 0.3f,
            BloomThreshold = 2.4f,
            Vignette = 0.45f,
            Grain = 0.01f,
            Aberration = 0.003f,
            Saturation = 1.06f,
            Gain = new Vector3(1.03f, 1f, 0.96f),
            Contrast = 1.06f,
            Sharpen = 0.2f,
        })
        {
            SunDirection = _keyDirection,
            SunColor = new Vector3(1f, 0.9f, 0.78f),
            NearPlane = 0.03f,
            FarPlane = 200f,
            Params0 = new Vector4(focus, 0f, 0f, 0f),
        };
    }

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        var frame = context.Frame;
        var constants = BaseConstants(frame);

        // Ombres de la fenêtre sur la table, les objets et le parquet proche.
        cmd.SetRenderTarget(null, _shadowMap);
        cmd.ClearDepth(_shadowMap!, 1f);
        cmd.SetPipeline(_shadowPipeline!);
        DrawScene(cmd, constants, glass: false, includeTable: true, includeFloor: false);
        cmd.Flush();

        // Reflets dans les vernis : la scène vue d'en dessous du plateau de la table, puis d'en dessous du parquet
        // (caméras symétriques). Mode léger : la table seulement.
        Reflect(cmd, frame, constants, _reflection!, _reflectionDepth!, TableTop, includeTable: false);
        if (!_light)
        {
            Reflect(cmd, frame, constants, _floorReflection!, _floorReflectionDepth!, 0f, includeTable: true);
        }

        var post = context.Post;
        cmd.SetRenderTargets([post.HdrColor, post.Velocity], post.Depth);
        cmd.ClearDepth(post.Depth, 1f);
        BindShared(cmd, reflection: true);
        cmd.SetPipeline(_skyPipeline!);
        cmd.SetConstants(1, constants);
        cmd.Draw(3);
        cmd.SetPipeline(_objectPipeline!);
        DrawScene(cmd, constants, glass: false, includeTable: true, includeFloor: true);
        cmd.Flush();

        // Ombres de contact : occlusion ambiante calculée d'après la profondeur, qui assombrit l'image.
        cmd.SetRenderTarget(post.HdrColor);
        cmd.SetPipeline(_occlusionPipeline!);
        cmd.SetTexture(8, post.Depth);
        cmd.SetConstants(1, constants);
        context.DrawFullscreenInBands(2);
        cmd.SetTexture(8, null);

        cmd.SetRenderTarget(post.HdrColor, post.Depth);
        cmd.SetPipeline(_glassPipeline!);
        DrawScene(cmd, constants, glass: true, includeTable: false, includeFloor: false);

        // Rayons de lumière dans la poussière, jusqu'à la surface vue à chaque pixel.
        cmd.SetRenderTarget(post.HdrColor);
        cmd.SetPipeline(_dustPipeline!);
        cmd.SetTexture(3, _shadowMap);
        cmd.SetTexture(8, post.Depth);
        cmd.SetConstants(1, constants);
        context.DrawFullscreenInBands(2);
        cmd.SetTexture(8, null);
        UnbindAll(cmd);
    }

    /// <summary>Flamme de la bougie (après l'anticrénelage), puis profondeur de champ sur l'image entière.</summary>
    public override void RenderOverlay(SceneContext context, ITexture target)
    {
        var cmd = context.Commands;
        var constants = BaseConstants(context.Frame);
        cmd.SetRenderTarget(target, context.Post.Depth);
        cmd.SetPipeline(_flamePipeline!);
        cmd.SetConstants(1, constants);
        cmd.Draw(6);

        cmd.CopyTexture(_focusSource!, target);
        cmd.SetRenderTarget(target);
        cmd.SetPipeline(_focusPipeline!);
        cmd.SetTexture(0, _focusSource);
        cmd.SetTexture(8, context.Post.Depth);
        cmd.SetConstants(1, constants);
        context.DrawFullscreenInBands(2);
        cmd.SetTexture(0, null);
        cmd.SetTexture(8, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var model in _models.Values)
            {
                model.Dispose();
            }

            foreach (var texture in _textures.Values)
            {
                texture.Dispose();
            }

            foreach (var disposable in new IDisposable?[]
            {
                _sky, _environment, _brdf, _shadowMap, _reflection, _reflectionDepth, _floorReflection, _floorReflectionDepth, _focusSource, _white, _flatNormal, _defaultSurface,
                _tableMesh, _floorMesh, _plinthMesh, _skyPipeline, _objectPipeline, _glassPipeline, _shadowPipeline, _dustPipeline, _occlusionPipeline,
                _flamePipeline, _focusPipeline, _reflectionSky, _reflectionObjects, _reflectionGlass, _prefilterPipeline, _lutPipeline,
            })
            {
                disposable?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static string Asset(string folder, string file) => System.IO.Path.Combine(AssetsFolder, folder, file);

    private SceneConstants BaseConstants(FrameConstants frame)
    {
        var center = new Vector3(0f, 0.9f, 0f);
        var lightView = Matrix4x4.CreateLookAt(center + (_keyDirection * 6f), center, Math.Abs(_keyDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
        var lightProj = Matrix4x4.CreateOrthographic(3.4f, 3.4f, 0.5f, 12f);
        var constants = new SceneConstants
        {
            World = Matrix4x4.Identity,
            ShadowViewProj = lightView * lightProj,
            BaseColor = Vector4.One,
            KeyLight = new Vector4(_keyDirection, 2.1f),
            KeyColor = new Vector4(1f, 0.9f, 0.78f, 1f / _shadowSize),
            Candle = new Vector4(_candle, 0.012f),
            Mirror = new Vector4(TableTop, 0f, 0f, 0f),
            Extra = new Vector4(0f, 0f, MathF.Max(frame.Params0.X, 0.2f), 0.55f),
            Atmosphere = new Vector4(0.0045f, 0f, 0f, 0f),
        };
        for (var i = 0; i < 9; i++)
        {
            constants.SH[i] = _sh[i];
        }

        return constants;
    }

    private void BindShared(ICommandList cmd, bool reflection)
    {
        cmd.SetTexture(3, _shadowMap);
        cmd.SetTexture(4, _environment);
        cmd.SetTexture(5, _brdf);
        cmd.SetTexture(6, reflection ? _reflection : null);
        cmd.SetTexture(7, _sky);
        cmd.SetTexture(10, reflection && !_light ? _floorReflection : null);
    }

    /// <summary>Une passe de reflet : la scène vue par la caméra symétrique du plan <paramref name="height"/>.</summary>
    private void Reflect(ICommandList cmd, FrameConstants frame, SceneConstants constants, ITexture target, ITexture depth, float height, bool includeTable)
    {
        cmd.SetRenderTargets([target], depth);
        cmd.Clear(target, new ColorF(0f, 0f, 0f, 0f));
        cmd.ClearDepth(depth, 1f);
        cmd.SetConstants(0, MirroredFrame(frame, height));
        var reflected = constants with { Mirror = new Vector4(height, 1f, 0f, 0f) };
        BindShared(cmd, reflection: false);
        cmd.SetPipeline(_reflectionSky!);
        cmd.SetConstants(1, reflected);
        cmd.Draw(3);
        cmd.SetPipeline(_reflectionObjects!);
        DrawScene(cmd, reflected, glass: false, includeTable: includeTable, includeFloor: false);
        cmd.SetPipeline(_reflectionGlass!);
        DrawScene(cmd, reflected, glass: true, includeTable: false, includeFloor: false);
        cmd.SetConstants(0, frame);
        cmd.Flush();
    }

    private static void UnbindAll(ICommandList cmd)
    {
        for (var i = 0; i < 11; i++)
        {
            cmd.SetTexture(i, null);
        }
    }

    /// <summary>Tous les objets de la scène ; <paramref name="glass"/> choisit les matières de verre (passe transparente).</summary>
    private void DrawScene(ICommandList cmd, SceneConstants constants, bool glass, bool includeTable, bool includeFloor)
    {
        if (!glass)
        {
            if (includeFloor)
            {
                DrawProcedural(cmd, _floorMesh!, constants, 3f, "parquet");
            }

            if (includeTable)
            {
                DrawProcedural(cmd, _tableMesh!, constants, 2f, "rosewood");
            }

            foreach (var plinth in _plinths)
            {
                DrawProcedural(cmd, _plinthMesh!, constants with { World = plinth }, 4f, null);
            }
        }

        foreach (var (model, world) in _objects)
        {
            foreach (var part in model.Parts)
            {
                var material = model.Materials[part.Material];
                if (material.Glass != glass)
                {
                    continue;
                }

                cmd.SetTexture(0, material.Base);
                cmd.SetTexture(1, material.Normal);
                cmd.SetTexture(2, material.Surface);
                cmd.SetConstants(1, constants with
                {
                    World = world,
                    BaseColor = material.Factor,
                    Surface = new Vector4(material.Metallic, material.Roughness, material.AoInRed ? 1f : 0f, glass ? 1f : 0f),
                });
                cmd.SetVertexBuffer(0, part.Vertices, Stride);
                cmd.SetIndexBuffer(part.Indices);
                cmd.DrawIndexed(part.Count);
            }
        }
    }

    private void DrawProcedural(ICommandList cmd, ProceduralMesh mesh, SceneConstants constants, float kind, string? textures)
    {
        cmd.SetTexture(0, textures is null ? _white : _textures[textures + ":base"]);
        cmd.SetTexture(1, textures is null ? _flatNormal : _textures[textures + ":normal"]);
        cmd.SetTexture(2, textures is null ? _defaultSurface : _textures[textures + ":surface"]);
        cmd.SetTexture(9, textures is null ? _defaultSurface : _textures[textures + ":height"]);
        cmd.SetConstants(1, constants with { Surface = new Vector4(0f, 1f, 1f, kind) });
        mesh.Draw(cmd);
    }

    /// <summary>Repère vu d'en dessous d'un plan horizontal : même image que la caméra, reflétée par ce plan.</summary>
    private static FrameConstants MirroredFrame(FrameConstants frame, float height)
    {
        var mirror = Matrix4x4.CreateScale(1f, -1f, 1f) * Matrix4x4.CreateTranslation(0f, 2f * height, 0f);
        var mirrored = frame;
        mirrored.View = mirror * frame.View;
        mirrored.ViewProj = mirror * frame.ViewProj;
        mirrored.ViewProjNoJitter = mirror * frame.ViewProjNoJitter;
        mirrored.PrevViewProjNoJitter = mirror * frame.PrevViewProjNoJitter;
        Matrix4x4.Invert(mirrored.ViewProj, out mirrored.InvViewProj);
        Matrix4x4.Invert(mirrored.View, out mirrored.InvView);
        mirrored.CameraPos = new Vector3(frame.CameraPos.X, (2f * height) - frame.CameraPos.Y, frame.CameraPos.Z);
        return mirrored;
    }

    /// <summary>Au chargement : environnement préfiltré (7 niveaux de rugosité, 6 faces) et table de la BRDF.</summary>
    private void Precompute(SceneContext context, ShaderCode fullscreen)
    {
        var device = context.Device;
        var cmd = context.Commands;
        // Gardés jusqu'à la fin de la scène : Direct3D 12 exécute ces commandes après la fin du chargement.
        var prefilter = _prefilterPipeline = device.CreatePipeline(PostProcess.Fullscreen("Matériaux : préfiltrage", fullscreen, context.Shaders.Get("materials.hlsl", "PrefilterPS", "ps_5_0"), PixelFormat.Rgba16Float, BlendMode.Opaque));
        var lut = _lutPipeline = device.CreatePipeline(PostProcess.Fullscreen("Matériaux : BRDF", fullscreen, context.Shaders.Get("materials.hlsl", "BrdfLutPS", "ps_5_0"), PixelFormat.Rg16Float, BlendMode.Opaque));
        var constants = BaseConstants(default);
        cmd.SetPipeline(prefilter);
        cmd.SetTexture(7, _sky);
        for (var mip = 0; mip < EnvironmentMips; mip++)
        {
            for (var face = 0; face < 6; face++)
            {
                cmd.SetRenderTarget(_environment, null, mip, face);
                cmd.SetConstants(1, constants with { Extra = new Vector4(face, mip / (float)(EnvironmentMips - 1), 1f, 0f) });
                cmd.Draw(3);
                cmd.Flush();
            }
        }

        cmd.SetTexture(7, null);
        cmd.SetRenderTarget(_brdf);
        cmd.SetPipeline(lut);
        cmd.SetConstants(1, constants);
        cmd.Draw(3);
        cmd.SetRenderTarget(null);
        cmd.Flush();
    }

    private ModelGpu LoadModel(IGpuDevice device, string name)
    {
        var folder = System.IO.Path.Combine(AssetsFolder, name);
        var file = Directory.EnumerateFiles(folder, "*.gltf").First();
        var model = GltfModel.Load(file);
        var gpu = new ModelGpu(model.Min, model.Max);
        foreach (var m in model.Materials)
        {
            gpu.Materials.Add(new MaterialGpu(
                m.BaseColor is { } b ? Texture(device, b, ImageRole.Color) : _white!,
                m.Normal is { } n ? Texture(device, n, ImageRole.Normal) : _flatNormal!,
                m.MetalRough is { } s ? Texture(device, s, ImageRole.Data) : _defaultSurface!,
                m.BaseColorFactor,
                m.Metallic,
                m.Roughness,
                m.AoInRed,
                m.Glass));
        }

        foreach (var p in model.Primitives)
        {
            gpu.Parts.Add(new ModelPart(
                device.CreateBuffer(new BufferDesc(p.Vertices.Length * 4L, BufferUsage.Vertex, Stride, name), MemoryMarshal.AsBytes(p.Vertices.AsSpan())),
                device.CreateBuffer(new BufferDesc(p.Indices.Length * 4L, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(p.Indices.AsSpan())),
                p.Indices.Length,
                p.Material));
        }

        return gpu;
    }

    private ITexture Texture(IGpuDevice device, string path, ImageRole role)
    {
        if (!_textures.TryGetValue(path, out var texture))
        {
            // Les images 2K perdent leur niveau le plus fin en mode léger (1K suffit en 720p).
            var skip = _light && path.Contains("_2k", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _textures[path] = texture = ImageAssets.LoadTexture(device, path, role, skip);
        }

        return texture;
    }

    /// <summary>Place les objets : sur la table (posés sur le plateau) et sur les socles tout autour.</summary>
    private void PlaceObjects()
    {
        foreach (var p in Table)
        {
            var model = _models[p.Model];
            var rotation = Matrix4x4.CreateRotationZ(p.Roll * MathF.PI / 180f) * Matrix4x4.CreateRotationY(p.Yaw * MathF.PI / 180f);
            var local = Matrix4x4.CreateScale(p.Scale) * rotation;
            var (low, center) = Footprint(model, local);
            var world = local * Matrix4x4.CreateTranslation(p.Position.X - center.X, TableTop - low, p.Position.Y - center.Y);
            _objects.Add((model, world));
            if (p.Model == "Lantern_01")
            {
                // La bougie, au cœur de la lanterne (un peu au-dessus du tiers de sa hauteur).
                var height = (model.Max.Y - model.Min.Y) * p.Scale;
                _candle = new Vector3(p.Position.X, TableTop + (height * 0.36f), p.Position.Y);
            }
        }

        for (var i = 0; i < PlinthModels.Length; i++)
        {
            var angle = (i + 0.5f) * MathF.Tau / PlinthModels.Length;
            var at = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 3.9f;
            _plinths.Add(Matrix4x4.CreateRotationY(-angle) * Matrix4x4.CreateTranslation(at.X, 0f, at.Y));
            var model = _models[PlinthModels[i]];
            var size = model.Max - model.Min;
            var scale = 0.75f / MathF.Max(size.Y, 0.2f);
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY((-angle * 180f / MathF.PI) + 180f);
            var (low, center) = Footprint(model, local);
            _objects.Add((model, local * Matrix4x4.CreateTranslation(at.X - center.X, 1.1f - low, at.Y - center.Y)));
        }
    }

    /// <summary>Bas de l'objet tourné (y minimal) et centre de son emprise au sol, d'après les coins de sa boîte.</summary>
    private static (float Low, Vector2 Center) Footprint(ModelGpu model, Matrix4x4 local)
    {
        var low = float.MaxValue;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var corner = new Vector3((k & 1) == 0 ? model.Min.X : model.Max.X, (k & 2) == 0 ? model.Min.Y : model.Max.Y, (k & 4) == 0 ? model.Min.Z : model.Max.Z);
            var p = Vector3.Transform(corner, local);
            low = MathF.Min(low, p.Y);
            min = Vector2.Min(min, new Vector2(p.X, p.Z));
            max = Vector2.Max(max, new Vector2(p.X, p.Z));
        }

        return (low, (min + max) * 0.5f);
    }

    private static Vector3[] Downsample(Vector3[] pixels, int width, int height, int factor, out int w, out int h)
    {
        w = width / factor;
        h = height / factor;
        var result = new Vector3[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = Vector3.Zero;
                for (var j = 0; j < factor; j++)
                {
                    for (var i = 0; i < factor; i++)
                    {
                        sum += pixels[(((y * factor) + j) * width) + (x * factor) + i];
                    }
                }

                result[(y * w) + x] = sum / (factor * factor);
            }
        }

        return result;
    }

    private static ITexture OnePixel(IGpuDevice device, byte r, byte g, byte b, byte a, PixelFormat format, string name)
    {
        var texture = device.CreateTexture(TextureDesc.Image(1, 1, format, name));
        device.UploadTexture(texture, 0, 0, [r, g, b, a], 4);
        return texture;
    }

    /// <summary>Plateau rond (dessus, chant), pied central et socle de la table, au format des modèles.</summary>
    private static ProceduralGeometry TableGeometry()
    {
        var g = new ProceduralGeometry();
        g.Cylinder(Vector3.Zero, TableRadius, TableTop - 0.045f, TableTop, 96);
        g.Cylinder(Vector3.Zero, 0.075f, 0.07f, TableTop - 0.045f, 32);
        g.Cylinder(Vector3.Zero, 0.42f, 0f, 0.07f, 64);
        return g;
    }

    private static ProceduralGeometry FloorGeometry()
    {
        var g = new ProceduralGeometry();
        g.Quad(new Vector3(-12f, 0f, -12f), new Vector3(12f, 0f, -12f), new Vector3(12f, 0f, 12f), new Vector3(-12f, 0f, 12f), Vector3.UnitY);
        return g;
    }

    private static ProceduralGeometry PlinthGeometry()
    {
        var g = new ProceduralGeometry();
        g.Box(new Vector3(0f, 0.04f, 0f), new Vector3(0.36f, 0.04f, 0.36f));
        g.Box(new Vector3(0f, 0.56f, 0f), new Vector3(0.28f, 0.5f, 0.28f));
        g.Box(new Vector3(0f, 1.08f, 0f), new Vector3(0.33f, 0.03f, 0.33f));
        return g;
    }

    private sealed record Placement(string Model, Vector2 Position, float Yaw, float Scale, float Roll = 0f);

    private sealed record ModelPart(IBuffer Vertices, IBuffer Indices, int Count, int Material);

    private sealed record MaterialGpu(ITexture Base, ITexture Normal, ITexture Surface, Vector4 Factor, float Metallic, float Roughness, bool AoInRed, bool Glass);

    private sealed class ModelGpu(Vector3 min, Vector3 max) : IDisposable
    {
        public Vector3 Min { get; } = min;

        public Vector3 Max { get; } = max;

        public List<ModelPart> Parts { get; } = [];

        public List<MaterialGpu> Materials { get; } = [];

        public void Dispose()
        {
            foreach (var part in Parts)
            {
                part.Vertices.Dispose();
                part.Indices.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 ShadowViewProj;
        public Vector4 BaseColor;
        public Vector4 Surface;
        public Vector4 KeyLight;
        public Vector4 KeyColor;
        public Vector4 Candle;
        public Vector4 Mirror;
        public Vector4 Extra;
        public Vector4 Atmosphere;
        public ShArray SH;
    }

    [System.Runtime.CompilerServices.InlineArray(9)]
    private struct ShArray
    {
        private Vector4 _element;
    }
}

/// <summary>Formes simples (cylindres, pavés, quadrilatères) au format de sommets des modèles glTF (48 octets).</summary>
internal sealed class ProceduralGeometry
{
    private readonly List<float> _vertices = [];
    private readonly List<uint> _indices = [];

    public float[] Vertices => [.. _vertices];

    public uint[] Indices => [.. _indices];

    /// <summary>Cylindre vertical (flanc, dessus, dessous) centré en <paramref name="center"/>.x/z.</summary>
    public void Cylinder(Vector3 center, float radius, float bottom, float top, int segments)
    {
        for (var s = 0; s < segments; s++)
        {
            var a0 = s * MathF.Tau / segments;
            var a1 = (s + 1) * MathF.Tau / segments;
            var d0 = new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0));
            var d1 = new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1));
            var p0 = center + (d0 * radius);
            var p1 = center + (d1 * radius);
            var start = (uint)(_vertices.Count / 12);
            Vertex(new Vector3(p0.X, bottom, p0.Z), d0);
            Vertex(new Vector3(p1.X, bottom, p1.Z), d1);
            Vertex(new Vector3(p1.X, top, p1.Z), d1);
            Vertex(new Vector3(p0.X, top, p0.Z), d0);
            _indices.AddRange([start, start + 2, start + 1, start, start + 3, start + 2]);
            Triangle(new Vector3(center.X, top, center.Z), new Vector3(p1.X, top, p1.Z), new Vector3(p0.X, top, p0.Z), Vector3.UnitY);
            Triangle(new Vector3(center.X, bottom, center.Z), new Vector3(p0.X, bottom, p0.Z), new Vector3(p1.X, bottom, p1.Z), -Vector3.UnitY);
        }
    }

    public void Box(Vector3 center, Vector3 half)
    {
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            foreach (var sign in new[] { 1f, -1f })
            {
                var n = axis * sign;
                var u = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
                var v = Vector3.Cross(n, u);
                var c = center + (n * Vector3.Dot(half, axis));
                var hu = u * Vector3.Dot(half, Abs(u));
                var hv = v * Vector3.Dot(half, Abs(v));
                Quad(c - hu - hv, c + hu - hv, c + hu + hv, c - hu + hv, n);
            }
        }
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        var start = (uint)(_vertices.Count / 12);
        foreach (var p in new[] { a, b, c, d })
        {
            Vertex(p, normal);
        }

        _indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        var start = (uint)(_vertices.Count / 12);
        Vertex(a, normal);
        Vertex(b, normal);
        Vertex(c, normal);
        _indices.AddRange([start, start + 1, start + 2]);
    }

    private void Vertex(Vector3 p, Vector3 n)
    {
        var t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY, n));
        _vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, t.X, t.Y, t.Z, 1f, p.X + p.Z, p.Y]);
    }

    private static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));
}

/// <summary>Une forme simple prête à dessiner.</summary>
internal sealed class ProceduralMesh(IBuffer vertices, IBuffer indices, int count) : IDisposable
{
    public static ProceduralMesh Create(IGpuDevice device, string name, ProceduralGeometry geometry)
    {
        var v = geometry.Vertices;
        var i = geometry.Indices;
        return new ProceduralMesh(
            device.CreateBuffer(new BufferDesc(v.Length * 4L, BufferUsage.Vertex, 48, name), MemoryMarshal.AsBytes(v.AsSpan())),
            device.CreateBuffer(new BufferDesc(i.Length * 4L, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(i.AsSpan())),
            i.Length);
    }

    public void Draw(ICommandList cmd)
    {
        cmd.SetVertexBuffer(0, vertices, 48);
        cmd.SetIndexBuffer(indices);
        cmd.DrawIndexed(count);
    }

    public void Dispose()
    {
        vertices.Dispose();
        indices.Dispose();
    }
}
