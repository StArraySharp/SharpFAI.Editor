using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Serialization;
using Thrust.Graphics;
using Thrust.Scene;

namespace SharpFAI.Player.Tests;

public sealed class TestImageDecorationSize
{
    [Test]
    public void OnlyDenseSequencesOfDistinctDecorationImagesAreStreamed()
    {
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration(),
            CreatePose() with { Image = "base", Visible = true, Smoothing = true }, new Vector2(2, 1), 0);
        var decoded = new Dictionary<string, DecorationImage>();
        for (int frame = 0; frame < 10; frame++)
        {
            item.Add(new MoveImageDecoration { Image = $"frame-{frame}" },
                new TrackTiming(frame * .04, 0, EventEnums.Ease.Linear), decoded);
        }
        item.Add(new MoveImageDecoration { Image = "interlude" },
            new TrackTiming(1, 0, EventEnums.Ease.Linear), decoded);
        for (int frame = 0; frame < 10; frame++)
        {
            item.Add(new MoveImageDecoration { Image = $"repeat-{frame % 2}" },
                new TrackTiming(2 + frame * .04, 0, EventEnums.Ease.Linear), decoded);
        }

        HashSet<string> images = [];
        List<ImageDecorationTimeline.AnimationFrame> uploads = [];
        item.CollectAnimationFrames(images, uploads, legacy: true);
        Assert.That(images, Is.EquivalentTo(Enumerable.Range(0, 10).Select(frame => $"frame-{frame}")));
        Assert.That(uploads.Select(frame => frame.Image), Is.EqualTo(Enumerable.Range(0, 10).Select(frame => $"frame-{frame}")));
        Assert.That(uploads.All(frame => frame.Smoothing && frame.RepeatX && !frame.RepeatY), Is.True);
    }

    [Test]
    public void PixelSizeUsesPlayerWorldUnitsAndAuthoredScale()
    {
        var pose = CreatePose();
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration { ScaleMultiplier = 2f }, pose, Vector2.One, 0);
        Assert.That(ImageDecorationRenderer.WorldSize(new Vector2(100, 80), pose, item, .5f, .25f),
            Is.EqualTo(new Vector2(200, 160)));
    }

    [Test]
    public void ScaleLockKeepsTheSamePixelSizeWhenZoomChanges()
    {
        var pose = CreatePose();
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration { LockScale = true }, pose, Vector2.One, 0);
        var initial = ImageDecorationRenderer.WorldSize(new Vector2(100, 80), pose, item, .5f, .5f) * .5f;
        var zoomed = ImageDecorationRenderer.WorldSize(new Vector2(100, 80), pose, item, .5f, .25f) * .25f;
        Assert.That(zoomed, Is.EqualTo(initial));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullParallaxPreservesLocalPivotOffset(bool text)
    {
        var pose = CreatePose() with
        {
            RelativeTo = "Tile", Pivot = new Vector2(-8.5f, 0f), Parallax = new Vector2(100f), Rotation = 90f,
        };
        AddImageDecoration source = text ? new AddTextDecoration() : new AddImageDecoration();
        source.LockRotation = true;
        source.LockScale = true;
        var item = new ImageDecorationTimeline.Item(source, pose, Vector2.One, 0);
        var camera = new Camera2D { Position = new Vector2(500f, 700f), Zoom = .25f };
        var model = ImageDecorationRenderer.ModelFor(new Vector2(100f, 80f), item, pose, camera, 100f, .5f,
            static (_, _) => (new Vector2(1000f, 2000f), Vector2.One, 0f), static _ => Vector2.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(model.Row3.X, Is.EqualTo(500f).Within(.001f));
            Assert.That(model.Row3.Y, Is.EqualTo(-1000f).Within(.001f));
        });
    }

    [Test]
    public void ViewportCullingKeepsRotatedQuadsThatCrossTheEdge()
    {
        var quad = Matrix4.CreateScale(.8f, .1f, 1f) * Matrix4.CreateRotationZ(MathHelper.PiOver4);

        Assert.Multiple(() =>
        {
            Assert.That(ImageDecorationRenderer.IsOutsideViewport(quad * Matrix4.CreateTranslation(1.2f, 0f, 0f)), Is.False);
            Assert.That(ImageDecorationRenderer.IsOutsideViewport(quad * Matrix4.CreateTranslation(0f, -1.4f, 0f)), Is.True);
        });
    }

    [Test]
    public void MoveRetainsScaleMultiplierAndAppliesOriginalSizeToReplacementTexture()
    {
        var pose = CreatePose();
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration { ScaleMultiplier = 2f }, pose, Vector2.One, 0);
        var timings = new TrackTiming(1, 1, EventEnums.Ease.Linear);
        var images = new Dictionary<string, DecorationImage>
        {
            ["large.png"] = new(200, 160, []),
        };
        item.Add(new MoveImageDecoration
        {
            Image = "large.png", OriginalSize = new JArray(100, 80), Scale = new JArray(50, 25),
        }, timings, images);

        var atEnd = item.At(2);
        var size = ImageDecorationRenderer.WorldSize(new Vector2(200, 160), atEnd, item, 1f, 1f);
        Assert.Multiple(() =>
        {
            Assert.That(atEnd.Image, Is.EqualTo("large.png"));
            Assert.That(size, Is.EqualTo(new Vector2(100, 40)));
            Assert.That(item.At(0).TextureScale, Is.EqualTo(Vector2.One));
        });
    }

    private static ImageDecorationTimeline.Pose CreatePose() => new("base.png", Vector2.Zero, Vector2.Zero,
        Vector2.One, Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One, 0f, 1f, -1, true, true, "Global");

    [Test]
    public void DecorationFrameStateHandlesFutureEventsTweensAndRewind()
    {
        var timeline = new ImageDecorationTimeline();
        timeline.Images.Add("base.png", new ImageDecoder.RgbaImage(16, 32, []));
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration(),
            CreatePose() with { Opacity = 0 }, Vector2.One, 0);
        timeline.Items.Add(item);
        item.Add(new MoveImageDecoration { Opacity = 100, Position = new JArray(8, 0) },
            new TrackTiming(2, 4, EventEnums.Ease.Linear), timeline.Images);
        item.Add(new MoveImageDecoration { Visible = false },
            new TrackTiming(7, 0, EventEnums.Ease.Linear), timeline.Images);

        var frame = new DecorationFrameState();
        Assert.That(frame.Update(timeline, 1, false), Is.Empty);
        Assert.That(frame.Update(timeline, 2, false), Is.Empty);
        Assert.That(frame.Update(timeline, 3, false).Single().Pose.Position.X, Is.EqualTo(2));
        Assert.That(frame.Update(timeline, 6, false).Single().Pose.Opacity, Is.EqualTo(1));
        Assert.That(frame.Update(timeline, 8, false), Is.Empty);
        Assert.That(frame.Update(timeline, 4, false).Single().Pose.Position.X, Is.EqualTo(4));
    }

    [Test]
    public void DecorationSeekKeepsIndependentOverlappingTweens()
    {
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration(), CreatePose(), Vector2.One, 0);
        var images = new Dictionary<string, DecorationImage>();
        item.Add(new MoveImageDecoration { Rotation = 90 }, new TrackTiming(1, 0, EventEnums.Ease.Linear), images);
        item.Add(new MoveImageDecoration { Position = new JArray(10, 0) }, new TrackTiming(2, 10, EventEnums.Ease.Linear), images);
        item.Add(new MoveImageDecoration { Opacity = 0 }, new TrackTiming(3, 1, EventEnums.Ease.Linear), images);

        var end = item.At(12);
        var earlier = item.At(5);
        Assert.Multiple(() =>
        {
            Assert.That(end.Position.X, Is.EqualTo(10));
            Assert.That(earlier.Position.X, Is.EqualTo(3).Within(.0001));
            Assert.That(earlier.Opacity, Is.Zero);
            Assert.That(earlier.Rotation, Is.EqualTo(90));
        });
    }

    [Test]
    public void LastPositionCapturesTheSampleBeforeCompletingThePreviousAxisTween()
    {
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration(), CreatePose(), Vector2.One, 0);
        var images = new Dictionary<string, DecorationImage>();
        item.Add(new MoveImageDecoration { Position = new JArray(10, 20) },
            new TrackTiming(0, 2, EventEnums.Ease.Linear), images);
        item.Add(new MoveImageDecoration { Position = new JArray(2, JValue.CreateNull()), RelativeTo = "LastPosition" },
            new TrackTiming(1, 1, EventEnums.Ease.Linear), images);

        Assert.Multiple(() =>
        {
            Assert.That(item.At(1).Position, Is.EqualTo(new Vector2(10, 10)));
            Assert.That(item.At(1.5).Position, Is.EqualTo(new Vector2(8.5f, 15)));
            Assert.That(item.At(3).Position, Is.EqualTo(new Vector2(7, 20)));
        });
    }

    [Test]
    public void MoveCanClearAnInitiallyBlankSpriteAndChangeItsMaskImmediately()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"","tag":"art","parallax":[0,0],"parallaxOffset":[5,5]}
            """, move: """
            {"floor":1,"eventType":"MoveDecorations","tag":"art","decorationImage":"",
             "maskingType":"Mask","maskingTarget":"receiver","parallaxOffset":[1,2],"duration":1}
            """)!;
        var item = timeline.Items.Single();
        var frame = new DecorationFrameState();
        Assert.That(frame.Update(timeline, 0, false), Has.Count.EqualTo(1));
        Assert.That(item.At(0).ParallaxOffset, Is.EqualTo(Vector2.Zero));
        Assert.That(frame.Update(timeline, .5, false), Is.Empty);
        Assert.That(item.At(.5).Masking, Is.EqualTo(DecorationMasking.Mask));
        Assert.That(item.At(.5).MaskingTarget, Is.EqualTo("receiver"));
        Assert.That(item.At(1).ParallaxOffset, Is.EqualTo(new Vector2(1, 2)));
        Assert.That(frame.Update(timeline, 0, false), Has.Count.EqualTo(1));
    }

    [Test]
    public void LegacyTilingFollowsHiddenSpriteAssignmentsAndRewind()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"old.png","tag":"art","tile":[2,1],"visible":false}
            """, legacyMasks: true, move: """
            {"floor":1,"eventType":"MoveDecorations","tag":"art","decorationImage":"new.png"}
            """)!;
        var frame = new DecorationFrameState();
        frame.Update(timeline, 0, false);
        Assert.That(frame.Repeat("old.png"), Is.EqualTo((true, false)));
        frame.Update(timeline, .5, false);
        Assert.That(frame.Repeat("old.png"), Is.EqualTo((false, false)));
        Assert.That(frame.Repeat("new.png"), Is.EqualTo((true, false)));
        frame.Update(timeline, 0, false);
        Assert.That(frame.Repeat("old.png"), Is.EqualTo((true, false)));
    }

    [TestCase(2, true)]
    [TestCase(12, true)]
    [TestCase(13, false)]
    public void LegacyActionDecorationsUseTheSameMigrationAsTheDecorationArray(int version, bool locksCamera)
    {
        const string decoration = """
            {"floor":0,"eventType":"AddDecoration","decText":"old.png","tag":"art","scale":[100,100],
             "components":"\"scrLockToCamera\":{\"lockPos\":true,\"lockRot\":true,\"lockScale\":true}"}
            """;
        var item = BuildTimeline(decoration, version: version, decorationInActions: true)!.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Initial.Image, Is.EqualTo("old.png"));
            Assert.That(item.Initial.Parallax, Is.EqualTo(locksCamera ? new Vector2(100f) : Vector2.Zero));
            Assert.That(item.LockRotation, Is.EqualTo(locksCamera));
            Assert.That(item.LockScale, Is.EqualTo(locksCamera));
            Assert.That(item.ScaleMultiplier, Is.EqualTo(locksCamera ? .5f : 1f));
        });
    }

    [TestCase(7, false)]
    [TestCase(8, true)]
    public void LegacyEnabledOnlyAffectsDecorationMovesInVersionSeven(int version, bool moves)
    {
        var item = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"","tag":"art"}
            """, move: """
            {"floor":1,"eventType":"MoveDecorations","tag":"art","positionOffset":[3,0],"enabled":false}
            """, version: version)!.Items.Single();
        Assert.That(item.At(1).Position.X, Is.EqualTo(moves ? 3f : 0f));
    }

    [Test]
    public void CropPivotMovesInstantlyWithoutChangingTheAuthoredPivotTween()
    {
        var item = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"","tag":"art","cropPivotOffset":[12,-8]}
            """, moves: JArray.Parse("""
            [
              {"floor":1,"eventType":"MoveDecorations","tag":"art","cropPivotOffset":[32,4],
               "pivotOffset":[2,0],"duration":2,"ease":"Linear"},
              {"floor":1,"eventType":"MoveDecorations","tag":"art","cropPivotOffset":[99,99],
               "disabled":{"cropPivotOffset":true},"angleOffset":90}
            ]
            """), version: 19)!.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.At(0).CropPivotOffset, Is.EqualTo(new Vector2(12f, -8f)));
            Assert.That(item.At(.5).CropPivotOffset, Is.EqualTo(new Vector2(32f, 4f)));
            Assert.That(item.At(.5).Pivot, Is.EqualTo(Vector2.Zero));
            Assert.That(item.At(1).CropPivotOffset, Is.EqualTo(new Vector2(32f, 4f)));
            Assert.That(item.At(1).Pivot.X, Is.EqualTo(1f).Within(.001f));
            Assert.That(item.At(0).CropPivotOffset, Is.EqualTo(new Vector2(12f, -8f)));
        });
    }

    [Test]
    public void CropPivotShiftsInSpriteUnitsBeforeRotationWithoutTextureScaling()
    {
        var pose = CreatePose() with
        {
            CropPivotOffset = new Vector2(12f, -8f), Scale = new Vector2(2f, .5f),
            TextureScale = new Vector2(.25f, 3f), Rotation = 90f,
        };
        var item = new ImageDecorationTimeline.Item(new AddImageDecoration { ScaleMultiplier = 3f }, pose, Vector2.One, 0);
        var model = ImageDecorationRenderer.ModelFor(new Vector2(100f, 80f), item, pose, new Camera2D(), 180f, 1f,
            static (_, _) => default, static _ => Vector2.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(model.Row3.X, Is.EqualTo(4f).Within(.001f));
            Assert.That(model.Row3.Y, Is.EqualTo(24f).Within(.001f));
        });
    }

    [Test]
    public void LegacyLockToCameraMigratesOnlyThroughVersionTwelve()
    {
        var decoration = JObject.Parse("""
            {"floor":0,"eventType":"AddDecoration","decText":"old.png","scale":[100,100],
             "components":"\"scrLockToCamera\":{\"lockPos\":true,\"lockRot\":true,\"lockScale\":true}"}
            """);
        var migrated = BuildTimeline(decoration.ToString(), version: 12)!.Items.Single();
        var newer = BuildTimeline(decoration.ToString(), version: 13)!.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(migrated.Initial.Image, Is.EqualTo("old.png"));
            Assert.That(migrated.Initial.Parallax, Is.EqualTo(new Vector2(100)));
            Assert.That(migrated.LockRotation, Is.True);
            Assert.That(migrated.LockScale, Is.True);
            Assert.That(migrated.ScaleMultiplier, Is.EqualTo(.5f));
            Assert.That(newer.LockScale, Is.False);
        });
    }

    [Test]
    public void ConditionalAndCollisionMovesChangeOnlyTheTriggeredDecorationTimeline()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"","tag":"one",
             "hitbox":"Event","hitboxEventTag":"contact"}
            """, moves: JArray.Parse("""
            [
              {"floor":0,"eventType":"SetConditionalEvents","perfectTag":"perfect"},
              {"floor":0,"eventType":"MoveDecorations","tag":"one","eventTag":"perfect","positionOffset":[4,null]},
              {"floor":0,"eventType":"MoveDecorations","tag":"one","eventTag":"contact","positionOffset":[null,6]},
              {"floor":1,"eventType":"MoveDecorations","tag":"one","positionOffset":[9,null]}
            ]
            """))!;
        var frame = new DecorationFrameState();
        var item = timeline.Items.Single();
        Assert.That(frame.Update(timeline, .2, false).Single().Pose.Position, Is.EqualTo(Vector2.Zero));
        timeline.TriggerCondition(0, HitMargin.Perfect, .2);
        timeline.TriggerCollision(["contact"], .2);
        Assert.That(frame.Update(timeline, .2, false).Single().Pose.Position, Is.EqualTo(new Vector2(4, 6)));
        Assert.That(item.At(.7).Position, Is.EqualTo(new Vector2(9, 6)));
        timeline.ResetTriggers();
        Assert.That(frame.Update(timeline, .2, false).Single().Pose.Position, Is.EqualTo(Vector2.Zero));
        Assert.That(item.At(.7).Position, Is.EqualTo(new Vector2(9, 0)));
    }

    [TestCase(false, 1.5, .5)]
    [TestCase(true, 1.125, .25)]
    public void FloorRepeatUsesTheBeatDistanceAndBpmOfItsOwningFloor(bool current, double start, double duration)
    {
        var floors = Enumerable.Range(0, 4).Select(_ => new Floor(0, 180, default)).ToArray();
        PlaybackEvent[] events =
        [
            new MoveImageDecoration { Floor = 1, EventTag = "repeat", AngleOffset = 90, Duration = 1 },
            new RepeatEvents { Floor = 1, Tag = "repeat", RepeatType = "Floor", FloorCount = 1, GapLength = 2,
                ExecuteOnCurrentFloor = current },
        ];
        var repeat = TimedEventSchedule.Build<MoveImageDecoration>(events, floors,
            [0, .5, .75, 1], [120, 120, 240, 240])[^1];
        Assert.That(repeat.Timing.Start, Is.EqualTo(start));
        Assert.That(repeat.Timing.Duration, Is.EqualTo(duration));
    }

    [Test]
    public void CameraAspectAppliesScreenAnchorAndWorldPivotSeparately()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","relativeTo":"CameraAspect","pivotOffset":[2,0],
             "position":[2,0],"rotation":90,"scale":[50,50],"parallax":[100,100],"parallaxOffset":[1,0]}
            """)!;
        var item = timeline.Items.Single();
        var camera = new Camera2D { Zoom = 1f };
        var model = ImageDecorationRenderer.ModelFor(new Vector2(100), item, item.Initial, camera, 150, 1f,
            static (_, _) => (Vector2.Zero, Vector2.One, 0f), static _ => Vector2.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(model.Row3.X, Is.EqualTo(.4f * camera.ViewportHeight / camera.ViewportWidth +
                300f / camera.WorldViewWidth).Within(.0001f));
            Assert.That(model.Row3.Y, Is.EqualTo(200f / camera.WorldViewHeight).Within(.0001f));
        });
    }

    [Test]
    public void ImageHitboxUsesRotatedGeometryAndPlanetOverlapRadius()
    {
        var box = DecorationHitbox.From(new AddImageDecoration { Hitbox = "Kill", HitboxRotation = 90 })!;
        Span<Vector2> points = stackalloc Vector2[4];
        box.FillVertices(Matrix4.CreateScale(160, 40, 1), new Vector2(160, 40), points);

        Assert.That(DecorationHitbox.TouchesPlanet(points, new Vector2(0, 105)), Is.True);
        Assert.That(DecorationHitbox.TouchesPlanet(points, new Vector2(51, 0)), Is.False);
    }

    [Test]
    public void HiddenDecorationStartsInvisible()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"hidden.png","visible":false}
            """);
        Assert.That(timeline, Is.Not.Null);
        Assert.That(timeline!.Items.Single().At(0).Visible, Is.False);
    }

    [Test]
    public void DecorationWithoutVisibleFlagStartsVisible()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"shown.png"}
            """);
        Assert.That(timeline!.Items.Single().At(0).Visible, Is.True);
    }

    [Test]
    public void BlendModeDecorationsAreKeptInsteadOfDropped()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"glow.png","blendMode":"Screen"}
            """);
        Assert.That(timeline, Is.Not.Null);
        Assert.That(timeline!.Items.Single().At(0).Blend, Is.EqualTo(DecorationBlendMode.Screen));
    }

    [Test]
    public void EveryAuthoredBlendModeMapsToItsEnum()
    {
        var expected = new (string Name, DecorationBlendMode Mode)[]
        {
            ("Screen", DecorationBlendMode.Screen),
            ("LinearDodge", DecorationBlendMode.LinearDodge),
            ("Overlay", DecorationBlendMode.Overlay),
            ("SoftLight", DecorationBlendMode.SoftLight),
            ("Difference", DecorationBlendMode.Difference),
            ("Multiply", DecorationBlendMode.Multiply),
        };
        foreach (var (name, mode) in expected)
        {
            var timeline = BuildTimeline($$"""
                {"floor":0,"eventType":"AddDecoration","decorationImage":"a.png","blendMode":"{{name}}"}
                """);
            Assert.That(timeline!.Items.Single().At(0).Blend, Is.EqualTo(mode), name);
        }
    }

    [Test]
    public void SyncFloorDepthIsCarriedIntoThePose()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"tile.png","relativeTo":"Tile","syncFloorDepth":true}
            """);
        Assert.That(timeline!.Items.Single().At(0).SyncFloorDepth, Is.True);
    }

    [Test]
    public void DecorationWithoutSyncFloorDepthKeepsItsOwnLayer()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"tile.png"}
            """);
        Assert.That(timeline!.Items.Single().At(0).SyncFloorDepth, Is.False);
    }

    [Test]
    public void MaskDecorationsAreKeptAndMarkedAsMaskSources()
    {
        var warnings = new List<string>();
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"mask.png","maskingType":"Mask","tag":"m1"}
            """, warnings);
        Assert.Multiple(() =>
        {
            Assert.That(timeline, Is.Not.Null);
            Assert.That(timeline!.Items.Single().At(0).Masking, Is.EqualTo(DecorationMasking.Mask));
            Assert.That(warnings, Is.Empty);
        });
    }

    [Test]
    public void MaskedDecorationsRecordTheirTargetTag()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"art.png","maskingType":"VisibleInsideMask","maskingTarget":"m1"}
            """);
        var pose = timeline!.Items.Single().At(0);
        Assert.Multiple(() =>
        {
            Assert.That(pose.Masking, Is.EqualTo(DecorationMasking.VisibleInsideMask));
            Assert.That(pose.MaskingTarget, Is.EqualTo("m1"));
        });
    }

    [TestCase(100, 100, 100, true)]
    [TestCase(100, 100, 20, false)]
    [TestCase(100, 100, -32767, false)]
    [TestCase(-1, -1, -1, true)]
    [TestCase(-1, -1, 0, false)]
    [TestCase(8, -5, -5, true)]
    [TestCase(8, -5, 0, true)]
    [TestCase(8, -5, 8, true)]
    [TestCase(8, -5, 9, false)]
    public void LegacyMaskDepthRangeIsInclusiveAndNormalizesReversedBounds(int front, int back, int depth, bool matches)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"mask.png","maskingType":"Mask",
             "useMaskingDepth":true,"maskingFrontDepth":{{front}},"maskingBackDepth":{{back}}}
            """, legacyMasks: true);
        var range = timeline!.Items.Single().At(0).MaskRange;
        Assert.That(range.Includes(DecorationMaskRange.DepthKey(depth, false)), Is.EqualTo(matches));
    }

    [Test]
    public void LegacyMaskRangeUsesDifferentSortLayersForSyncedFloorAndDepthZero()
    {
        var background = default(DecorationMaskRange).WithChanges(true, 0, 100);
        var crossing = background.WithChanges(null, -1, null);
        long syncedFloor = DecorationMaskRange.DepthKey(0, true);
        Assert.Multiple(() =>
        {
            Assert.That(background.Includes(DecorationMaskRange.DepthKey(0, false)), Is.True);
            Assert.That(background.Includes(syncedFloor), Is.False);
            Assert.That(crossing.Includes(syncedFloor), Is.True);
            Assert.That(default(DecorationMaskRange).Includes(syncedFloor), Is.True);
        });
    }

    [Test]
    public void MoveChangesMaskBoundsAtEventStartAndRespectsDisabledFields()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"mask.png","maskingType":"Mask","tag":"door",
             "useMaskingDepth":false,"maskingFrontDepth":20,"maskingBackDepth":100}
            """, legacyMasks: true, move: """
            {"floor":1,"eventType":"MoveDecorations","tag":"door","duration":8,"ease":"Linear",
             "useMaskingDepth":true,"maskingFrontDepth":50,"maskingBackDepth":90,"disabled":{"maskingBackDepth":true}}
            """);
        var item = timeline!.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.At(0).MaskRange.Includes(DecorationMaskRange.DepthKey(-32767, false)), Is.True);
            Assert.That(item.At(.5).MaskRange, Is.EqualTo(new DecorationMaskRange(true, 50, 100)));
            Assert.That(item.At(1).MaskRange.Includes(DecorationMaskRange.DepthKey(20, false)), Is.False);
        });
    }

    [TestCase("", true, false, true)]
    [TestCase("NO TAG", true, false, true)]
    [TestCase("", false, false, false)]
    [TestCase("receiver", true, false, false)]
    [TestCase("receiver", false, true, true)]
    public void ZeroOpacityMaskKeepsExplicitDepthCompatibility(string target, bool useRange, bool legacy, bool expected)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddDecoration","opacity":0,"maskingType":"Mask","maskingTarget":"{{target}}",
             "useMaskingDepth":{{useRange.ToString().ToLowerInvariant()}},"maskingFrontDepth":-1000,"maskingBackDepth":-1000}
            """, legacyMasks: legacy)!;
        Assert.That(new DecorationFrameState().Update(timeline, 0, false).Count, Is.EqualTo(expected ? 1 : 0));
    }

    [TestCase("", true)]
    [TestCase("NO TAG", true)]
    [TestCase("receiver", false)]
    public void VisibleMaskWithoutCustomDepthUsesLegacyClippingOnlyWithoutATarget(string target, bool expected)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddDecoration","opacity":25,"maskingType":"Mask","maskingTarget":"{{target}}"}
            """)!;
        Assert.That(timeline.Items.Single().At(0).UsesDepthMask(legacy: false), Is.EqualTo(expected));
    }

    [Test]
    public void OutsideMaskingUsesItsOwnMode()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddDecoration","decorationImage":"art.png","maskingType":"VisibleOutsideMask","maskingTarget":"m1"}
            """);
        Assert.That(timeline!.Items.Single().At(0).Masking, Is.EqualTo(DecorationMasking.VisibleOutsideMask));
    }

    [Test]
    public void FontNamesMapToTheFamiliesTheGameUses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextDecorationImage.ResolveFamily("ComicSansMS"), Is.EqualTo("Comic Sans MS"));
            Assert.That(TextDecorationImage.ResolveFamily("CourierNew"), Is.EqualTo("Courier New"));
            Assert.That(TextDecorationImage.ResolveFamily("Georgia"), Is.EqualTo("Georgia"));
            Assert.That(TextDecorationImage.ResolveFamily("Impact"), Is.EqualTo("Impact"));
            Assert.That(TextDecorationImage.ResolveFamily("TimesNewRoman"), Is.EqualTo("Times New Roman"));
            Assert.That(TextDecorationImage.ResolveFamily("Default"), Is.EqualTo("Arial"));
            Assert.That(TextDecorationImage.ResolveFamily("Arial"), Is.EqualTo("Arial"));
            Assert.That(TextDecorationImage.ResolveFamily(null), Is.EqualTo("Arial"));
        });
    }

    [Test]
    public void LineSpacingFollowsTheLanguageOfTheText()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextDecorationImage.LineSpacing("hello"), Is.EqualTo(TextDecorationImage.LatinLineSpacing));
            Assert.That(TextDecorationImage.LineSpacing("你好"), Is.EqualTo(TextDecorationImage.CjkLineSpacing));
            Assert.That(TextDecorationImage.LineSpacing("こんにちは"), Is.EqualTo(TextDecorationImage.JapaneseLineSpacing));
        });
    }

    [Test]
    public void TextRasterKeepsAuthoredNewlinesAndStripsRichTags()
    {
        var oneLine = TextDecorationImage.Rasterize("hello", "Default");
        var twoLines = TextDecorationImage.Rasterize("hello\nworld", "Default");
        var tagged = TextDecorationImage.Rasterize("<b>hello</b>", "Default");
        var untagged = TextDecorationImage.Rasterize("hello", "Default");
        Assert.Multiple(() =>
        {
            Assert.That(twoLines.Height, Is.GreaterThan(oneLine.Height), "newlines must add height");
            Assert.That(tagged.Width, Is.EqualTo(untagged.Width));
            Assert.That(tagged.Height, Is.EqualTo(untagged.Height));
        });
    }

    [Test]
    public void EmptyTextStillProducesAUsableRaster()
    {
        var image = TextDecorationImage.Rasterize("", "Default");
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.GreaterThan(0));
            Assert.That(image.Height, Is.GreaterThan(0));
        });
    }

    [Test]
    public void LongSubtitleOverflowsHorizontallyOnOneLine()
    {
        var shortLine = TextDecorationImage.Rasterize("hello", "Georgia");
        var longLine = TextDecorationImage.Rasterize("\"anybody here to save me?\"", "Georgia");
        Assert.Multiple(() =>
        {
            Assert.That(longLine.Width, Is.GreaterThan(960));
            Assert.That(longLine.Height, Is.EqualTo(shortLine.Height));
        });
    }

    [Test]
    public void LocalizedTextPrefersEnglishThenAnyString()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextDecorationImage.ReadText(JValue.CreateString("plain")), Is.EqualTo("plain"));
            Assert.That(TextDecorationImage.ReadText(JObject.Parse("""{"en":"English","ko":"Korean"}""")),
                Is.EqualTo("English"));
            Assert.That(TextDecorationImage.ReadText(JObject.Parse("""{"ko":"Korean"}""")), Is.EqualTo("Korean"));
            Assert.That(TextDecorationImage.ReadText(null), Is.Empty);
        });
    }

    [TestCase(1)]
    [TestCase(15)]
    public void SetTextSwapsTheRasterWithoutStretchingGlyphs(int chartVersion)
    {
        var root = new JObject
        {
            ["song"] = "test",
            ["bpm"] = 120,
            ["offset"] = 0,
            ["angleData"] = new JArray(0, 0),
            ["settings"] = new JObject { ["version"] = chartVersion },
            ["actions"] = new JArray(JObject.Parse("""
                {"floor":1,"eventType":"SetText","decText":"a much longer piece of text","tag":"t1","duration":0,"ease":"Linear"}
                """)),
            ["decorations"] = new JArray(JObject.Parse("""
                {"floor":0,"eventType":"AddText","decText":"x","font":"Default","tag":"t1","scale":[60,60]}
                """)),
        };
        var level = new Level(root.Properties().ToDictionary(item => item.Name, item => (object)item.Value));
        var floors = new[] { new Floor(0, 180, default), new Floor(0, 180, default) };
        var events = PlaybackEventBinder.BindForPlayback(level);
        var timeline = ImageDecorationTimeline.Build(events, floors, [0, 0.5], [120, 120], null, [], CancellationToken.None);
        Assert.That(timeline, Is.Not.Null);

        var item = timeline!.Items.Single();
        var before = item.At(0);
        var after = item.At(1);
        Assert.Multiple(() =>
        {
            Assert.That(after.Image, Is.Not.EqualTo(before.Image), "SetText must swap the raster");
            Assert.That(timeline.Images[after.Image].Width, Is.GreaterThan(timeline.Images[before.Image].Width));
            Assert.That(after.TextureScale, Is.EqualTo(before.TextureScale));
        });
    }

    [Test]
    public void TextDecorationScalesLikeA100PpuSpriteInsteadOfOneUnitPerPixel()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddText","decText":"x4","font":"Default","relativeTo":"Tile"}
            """);
        Assert.That(timeline, Is.Not.Null);

        var item = timeline!.Items.Single();
        var pose = item.At(0);
        var raster = timeline.Images[pose.Image];
        Vector2 pixels = new(raster.Width, raster.Height);
        Assert.That(ImageDecorationRenderer.WorldSize(pixels, pose, item, 1f, 1f), Is.EqualTo(pixels));
    }

    [TestCase("Floor")]
    [TestCase("Planet")]
    [TestCase("PlayerBubble")]
    public void ObjectDecorationLoadsAndMovesWithoutReplacingItsArt(string objectType)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddObject","objectType":"{{objectType}}","tag":"obj","trackAngle":90}
            """, move: """
            {"floor":1,"eventType":"MoveDecorations","tag":"obj","positionOffset":[2,1],"decorationImage":"unrelated.png"}
            """, version: 19)!;
        var item = timeline.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.IsObject, Is.True);
            Assert.That(timeline.Images.ContainsKey(item.Initial.Image), Is.True);
            Assert.That(item.At(1).Image, Is.EqualTo(item.Initial.Image));
            Assert.That(item.At(1).Position, Is.EqualTo(new Vector2(2, 1)));
        });
    }

    [Test]
    public void PlanetDecorationUsesOneFrameAtATime()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddObject","objectType":"Planet"}
            """, version: 19)!;
        string first = ObjectDecorationArt.PlanetImageAt(0);
        string second = ObjectDecorationArt.PlanetImageAt(.1);
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(ObjectDecorationArt.PlanetImageAt(1), Is.EqualTo(second));
            Assert.That(timeline.Images[first].Height, Is.EqualTo(80));
            Assert.That(timeline.Images[second].Height, Is.EqualTo(timeline.Images[first].Height));
            Assert.That(timeline.Images[second].Pixels, Is.Not.EqualTo(timeline.Images[first].Pixels));
        });
    }

    [TestCase("Circle", 500f)]
    [TestCase("Rectangle", 0f)]
    public void ParticleShapeControlsDefaultSpeed(string shape, float speed)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddParticle","tag":"dust","shapeType":"{{shape}}","autoPlay":false}
            """, move: """
            {"floor":0,"eventType":"EmitParticle","tag":"dust","count":1}
            """, version: 19)!;
        List<ParticleDecorationTimeline.Sample> samples = [];
        timeline.Items.Single().Particles!.Collect(.5, samples);
        Assert.That(samples.Single().Travel.Length * 150f, Is.EqualTo(speed * .5f).Within(.001f));
    }

    [TestCase("\"ff0000\"")]
    [TestCase("""{"mode":"Color","color1":"ff0000"}""")]
    [TestCase("""{"mode":0,"color1":"ff0000"}""")]
    public void ParticleCommandsAreTaggedAndDeterministicAcrossRewind(string color)
    {
        var timeline = BuildTimeline($$"""
            {"floor":0,"eventType":"AddParticle","tag":"dust","autoPlay":false,"emissionRate":[4,4],
             "particleLifetime":[4,4],"color":{{color}},"randomSeed":42}
            """, moves: JArray.Parse("""
            [
              {"floor":0,"eventType":"EmitParticle","tag":"other","count":8},
              {"floor":0,"eventType":"EmitParticle","tag":"dust","count":3},
              {"floor":0,"eventType":"SetParticle","tag":"dust","targetMode":"Start"},
              {"floor":1,"eventType":"SetParticle","tag":"dust","targetMode":"Stop"},
              {"floor":1,"eventType":"SetParticle","tag":"dust","targetMode":"Clear","angleOffset":180}
            ]
            """), version: 19)!;
        var item = timeline.Items.Single();
        var particles = item.Particles!;
        List<ParticleDecorationTimeline.Sample> samples = [];
        particles.Collect(.25, samples);
        var before = samples.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Images.ContainsKey(item.Initial.Image), Is.True);
            Assert.That(before, Has.Length.EqualTo(4));
            Assert.That(before[0].Tint, Is.EqualTo(new Vector4(1, 0, 0, 1)));
            particles.Collect(.75, samples);
            Assert.That(samples, Has.Count.EqualTo(4));
            particles.Collect(1.25, samples);
            Assert.That(samples, Is.Empty);
            particles.Collect(.25, samples);
            Assert.That(samples, Is.EqualTo(before));
        });
    }

    [Test]
    public void ParticleLifetimeGradientMultipliesStartTintAndPreservesLowAlpha()
    {
        var timeline = BuildTimeline("""
            {"floor":0,"eventType":"AddParticle","tag":"stars","autoPlay":false,"particleLifetime":[2,2],
             "color":{"mode":"RandomColor","gradient1":{"colorKeys":[{"time":0,"color":"ff8000"}]}},
             "colorOverLifetime":{"mode":"Gradient","gradient1":{"mode":"Blend",
              "colorKeys":[{"time":0,"color":"ffffff"},{"time":1,"color":"00ffff"}],
              "alphaKeys":[{"time":1,"alpha":0},{"time":0,"alpha":0},{"time":0.5,"alpha":0.01}]}}}
            """, move: """
            {"floor":0,"eventType":"EmitParticle","tag":"stars","count":1}
            """, version: 19)!;
        var particles = timeline.Items.Single().Particles!;
        List<ParticleDecorationTimeline.Sample> samples = [];
        particles.Collect(0, samples);
        Assert.That(samples.Single().Tint.W, Is.Zero);
        particles.Collect(1, samples);
        var peak = samples.Single();
        Assert.Multiple(() =>
        {
            Assert.That(peak.Tint.X, Is.EqualTo(.5f).Within(.0001f));
            Assert.That(peak.Tint.Y, Is.EqualTo(128f / 255).Within(.0001f));
            Assert.That(peak.Tint.Z, Is.Zero);
            Assert.That(peak.Tint.W, Is.EqualTo(.01f).Within(.0001f));
        });
        particles.Collect(1.5, samples);
        Assert.That(samples.Single().Tint.W, Is.EqualTo(.005f).Within(.0001f));
        particles.Collect(1, samples);
        Assert.That(samples.Single(), Is.EqualTo(peak));
    }

    private static ImageDecorationTimeline? BuildTimeline(string decoration, List<string>? warnings = null,
        bool legacyMasks = false, string? move = null, JArray? moves = null, int version = 15, bool decorationInActions = false)
    {
        var actions = moves ?? (move is null ? new JArray() : new JArray(JObject.Parse(move)));
        var source = JObject.Parse(decoration);
        if (decorationInActions)
            actions.Insert(0, source);
        var root = new JObject
        {
            ["song"] = "test",
            ["bpm"] = 120,
            ["offset"] = 0,
            ["angleData"] = new JArray(0, 0),
            ["settings"] = new JObject { ["version"] = version, ["zoom"] = 120 },
            ["actions"] = actions,
            ["decorations"] = decorationInActions ? new JArray() : new JArray(source),
        };
        var level = new Level(root.Properties().ToDictionary(item => item.Name, item => (object)item.Value));
        var floors = new[] { new Floor(0, 180, default), new Floor(0, 180, default) };
        var events = PlaybackEventBinder.BindForPlayback(level);
        return ImageDecorationTimeline.Build(events, floors, [0, 0.5], [120, 120], null,
            warnings ?? [], CancellationToken.None, legacyMasks);
    }
}
