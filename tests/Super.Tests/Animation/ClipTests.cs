using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

public class ClipTests
{
    private const float Tolerance = 0.0001f;

    [Fact]
    public void Lerp_moves_position_and_turns_the_short_way()
    {
        var from = JointPose.Identity;
        var to = new JointPose(new Vector3(2f, 0f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One);

        var half = JointPose.Lerp(in from, in to, 0.5f);

        Assert.Equal(1f, half.Position.X, Tolerance);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(expected, half.Rotation)), Tolerance);
    }

    [Fact]
    public void Lerp_towards_a_negated_quaternion_does_not_go_the_long_way()
    {
        var from = new JointPose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f), Vector3.One);
        var to = new JointPose(Vector3.Zero, -Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f), Vector3.One);

        var half = JointPose.Lerp(in from, in to, 0.5f);

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(expected, half.Rotation)), Tolerance);
    }

    [Fact]
    public void Sample_interpolates_between_frames_and_clamps_at_the_ends()
    {
        var clip = TestRig.Move();
        var pose = new JointPose[2];

        clip.Sample(0.5f, pose);
        Assert.Equal(1f, pose[0].Position.X, Tolerance);

        clip.Sample(5f, pose);
        Assert.Equal(4f, pose[0].Position.X, Tolerance);

        clip.Sample(-1f, pose);
        Assert.Equal(0f, pose[0].Position.X, Tolerance);
    }

    [Fact]
    public void Duration_is_the_time_of_the_last_frame()
    {
        Assert.Equal(2f, TestRig.Move().Duration);
        Assert.Equal(0f, new AnimationClip("still", 30f, 1, [JointPose.Identity]).Duration);
    }

    [Fact]
    public void Blend_moves_only_masked_joints()
    {
        JointPose[] into = [TestRig.RootAt(0f), TestRig.ChildAt(0f)];
        JointPose[] from = [TestRig.RootAt(2f), TestRig.ChildAt(2f)];

        PoseBlend.Blend(into, from, 0.5f, [1f, 0f]);

        Assert.Equal(1f, into[0].Position.X, Tolerance);
        Assert.Equal(0f, into[1].Position.X, Tolerance);
    }

    [Fact]
    public void Blend_without_a_mask_moves_every_joint()
    {
        JointPose[] into = [TestRig.RootAt(0f), TestRig.ChildAt(0f)];
        JointPose[] from = [TestRig.RootAt(2f), TestRig.ChildAt(4f)];

        PoseBlend.Blend(into, from, 0.25f);

        Assert.Equal(0.5f, into[0].Position.X, Tolerance);
        Assert.Equal(1f, into[1].Position.X, Tolerance);
    }

    [Fact]
    public void Skin_matrices_carry_a_child_with_its_parent()
    {
        var skeleton = TestRig.Skeleton();
        JointPose[] locals = [TestRig.RootAt(1f), TestRig.ChildRest];
        var skin = new Matrix4x4[2];

        skeleton.SkinMatrices(locals, skin);

        // A vertex at the child's bind position moves with the root's slide.
        var moved = Vector3.Transform(new Vector3(0f, 1f, 0f), skin[1]);
        Assert.Equal(1f, moved.X, Tolerance);
        Assert.Equal(1f, moved.Y, Tolerance);
        Assert.Equal(0f, moved.Z, Tolerance);
    }

    [Fact]
    public void Skin_matrices_at_rest_leave_vertices_alone()
    {
        var skeleton = TestRig.Skeleton();
        var skin = new Matrix4x4[2];

        skeleton.SkinMatrices(skeleton.Rest, skin);

        Assert.Equal(Matrix4x4.Identity, skin[0]);
        Assert.Equal(Matrix4x4.Identity, skin[1]);
    }

    [Fact]
    public void Skin_matrices_apply_the_root_transform_last()
    {
        var skeleton = new Skeleton(
            [TestRig.Root],
            [-1],
            [JointPose.Identity],
            [Matrix4x4.Identity],
            Matrix4x4.CreateScale(2f));
        var skin = new Matrix4x4[1];

        skeleton.SkinMatrices([TestRig.RootAt(1f)], skin);

        Assert.Equal(2f, Vector3.Transform(Vector3.Zero, skin[0]).X, Tolerance);
    }

    [Fact]
    public void Mark_descendants_marks_a_joint_and_everything_under_it()
    {
        var skeleton = TestRig.Skeleton();
        var marks = new bool[2];

        skeleton.MarkDescendants(1, marks);
        Assert.Equal([false, true], marks);

        skeleton.MarkDescendants(0, marks);
        Assert.Equal([true, true], marks);
    }

    [Fact]
    public void A_model_file_keeps_its_skeleton_and_clips()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(folder, "rig.rmodel");
        try
        {
            var vertex = new Vertex();
            vertex.SetSkin([(1, 1f)]);
            new ModelData
            {
                Vertices = [vertex, vertex, vertex],
                Indices = [0, 1, 2],
                Parts = [new ModelPart { FirstIndex = 0, IndexCount = 3, Material = 0 }],
                Materials = [new ModelMaterial { BaseColor = Vector4.One }],
                Textures = [],
                Skeleton = TestRig.Skeleton(),
                Clips = [TestRig.Move()],
            }.Write(path);

            var (skeleton, clips) = ModelData.ReadRig(path);

            Assert.NotNull(skeleton);
            Assert.Equal([TestRig.Root, TestRig.Child], skeleton.Names);
            Assert.Equal([-1, 0], skeleton.Parents);
            Assert.Equal(1f, skeleton.Rest[1].Position.Y);
            Assert.Equal(-1f, skeleton.InverseBind[1].M42);
            var clip = Assert.Single(clips);
            Assert.Equal("move", clip.Name);
            Assert.Equal(2f, clip.Duration);
            Assert.Equal(4f, clip.Frames[4].Position.X);

            var model = ModelData.Read(path);
            Assert.Equal(1u, model.Vertices[0].Joints & 0xFF);
            Assert.Equal(255u, model.Vertices[0].Weights & 0xFF);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
