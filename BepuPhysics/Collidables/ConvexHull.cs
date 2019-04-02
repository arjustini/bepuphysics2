﻿using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;

namespace BepuPhysics.Collidables
{
    public struct HullBoundingPlanes
    {
        /// <summary>
        /// Normal of the bounding plane.
        /// </summary>
        public Vector3Wide Normal;
        /// <summary>
        /// Offset from the origin to a point on the plane along the normal. 
        /// </summary>
        public Vector<float> Offset;
    }


    public struct HullVertexIndex
    {
        //This means you can only have Vector<float>.Count * 65536 points in a convex hull. Oh no!
        public ushort BundleIndex;
        public ushort InnerIndex;

        public override string ToString()
        {
            return $"({BundleIndex}, {InnerIndex})";
        }
    }

    public struct ConvexHull : IConvexShape
    {
        /// <summary>
        /// Bundled points of the convex hull.
        /// </summary>
        public Buffer<Vector3Wide> Points;
        /// <summary>
        /// Bundled bounding planes of the convex hull. 
        /// </summary>
        public Buffer<HullBoundingPlanes> BoundingPlanes;
        /// <summary>
        /// Combined set of vertices used by each face. Use FaceToVertexIndicesStart to index into this for a particular face.
        /// </summary>
        public Buffer<HullVertexIndex> FaceVertexIndices;
        /// <summary>
        /// Start indices of faces in the FaceVertexIndices.
        /// </summary>
        public Buffer<int> FaceToVertexIndicesStart;

        /// <summary>
        /// Creates a convex hull from a point set.
        /// </summary>
        /// <param name="points">Points to compute the convex hull of.</param>
        /// <param name="pool">Pool in which to allocate the convex hull and any temporary resources needed to compute the hull.</param>
        /// <param name="center">Computed center of the convex hull before the hull was recentered.</param>
        public ConvexHull(Buffer<Vector3> points, BufferPool pool, out Vector3 center)
        {
            ConvexHullHelper.CreateShape(points, pool, out center, out this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetVertexIndicesForFace(int faceIndex, out Buffer<HullVertexIndex> faceVertexIndices)
        {
            var start = FaceToVertexIndicesStart[faceIndex];
            var nextFaceIndex = faceIndex + 1;
            var end = nextFaceIndex == FaceToVertexIndicesStart.Length ? FaceVertexIndices.Length : FaceToVertexIndicesStart[nextFaceIndex];
            var count = end - start;
            FaceVertexIndices.Slice(start, count, out faceVertexIndices);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetPoint(HullVertexIndex pointIndex, out Vector3 point)
        {
            Vector3Wide.ReadSlot(ref Points[pointIndex.BundleIndex], pointIndex.InnerIndex, out point);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetPoint(int pointIndex, out Vector3 point)
        {
            BundleIndexing.GetBundleIndices(pointIndex, out var bundleIndex, out var innerIndex);
            Vector3Wide.ReadSlot(ref Points[bundleIndex], innerIndex, out point);

        }

        //TODO: With platform intrinsics, we could improve the 'horizontal' parts of these functions.
        public void ComputeAngularExpansionData(out float maximumRadius, out float maximumAngularExpansion)
        {
            Vector<float> maximumRadiusSquaredWide = default;
            Vector<float> minimumRadiusSquaredWide = new Vector<float>(float.MaxValue);
            for (int i = 0; i < Points.Length; ++i)
            {
                Vector3Wide.LengthSquared(Points[i], out var candidate);
                maximumRadiusSquaredWide = Vector.Max(candidate, maximumRadiusSquaredWide);
                minimumRadiusSquaredWide = Vector.Min(candidate, minimumRadiusSquaredWide);
            }
            var minimumRadiusWide = Vector.SquareRoot(minimumRadiusSquaredWide);
            var maximumRadiusWide = Vector.SquareRoot(maximumRadiusSquaredWide);
            maximumRadius = maximumRadiusWide[0];
            float minimumRadius = minimumRadiusWide[0];
            for (int i = 1; i < Vector<float>.Count; ++i)
            {
                var maxCandidate = maximumRadiusWide[i];
                var minCandidate = minimumRadiusWide[i];
                if (maxCandidate > maximumRadius)
                    maximumRadius = maxCandidate;
                if (minCandidate < minimumRadius)
                    minimumRadius = minCandidate;
            }
            maximumAngularExpansion = maximumRadius - minimumRadius;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ComputeBounds(in QuaternionWide orientationWide, out Vector3 min, out Vector3 max)
        {
            Matrix3x3Wide.CreateFromQuaternion(orientationWide, out var orientationMatrix);
            Vector3Wide minWide = default, maxWide = default;
            for (int i = 0; i < Points.Length; ++i)
            {
                Matrix3x3Wide.TransformWithoutOverlap(Points[i], orientationMatrix, out var p);
                Vector3Wide.Min(minWide, p, out minWide);
                Vector3Wide.Max(maxWide, p, out maxWide);
            }
            Vector3Wide.ReadFirst(minWide, out min);
            Vector3Wide.ReadFirst(maxWide, out max);
            for (int i = 1; i < Vector<float>.Count; ++i)
            {
                //TODO: Check codegen. Bounds checks elided?
                var minCandidate = new Vector3(minWide.X[i], minWide.Y[i], minWide.Z[i]);
                var maxCandidate = new Vector3(maxWide.X[i], maxWide.Y[i], maxWide.Z[i]);
                min = Vector3.Min(minCandidate, min);
                max = Vector3.Max(maxCandidate, max);
            }
        }

        public void ComputeBounds(in BepuUtilities.Quaternion orientation, out Vector3 min, out Vector3 max)
        {
            QuaternionWide.Broadcast(orientation, out var orientationWide);
            ComputeBounds(orientationWide, out min, out max);
        }

        public struct ConvexHullTriangleSource : ITriangleSource
        {
            ConvexHull hull;
            int faceIndex;
            int subtriangleIndex;

            public ConvexHullTriangleSource(in ConvexHull hull)
            {
                this.hull = hull;
                faceIndex = 0;
                subtriangleIndex = 2;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool GetNextTriangle(out Vector3 a, out Vector3 b, out Vector3 c)
            {
                //This isn't quite as direct or fast as it could be, but it's fairly simple without requiring a redundant implementation.
                if (faceIndex < hull.FaceToVertexIndicesStart.Length)
                {
                    hull.GetVertexIndicesForFace(faceIndex, out var faceIndices);
                    hull.GetPoint(faceIndices[0], out a);
                    hull.GetPoint(faceIndices[subtriangleIndex - 1], out b);
                    hull.GetPoint(faceIndices[subtriangleIndex], out c);
                    ++subtriangleIndex;
                    if (subtriangleIndex == faceIndices.Length)
                    {
                        subtriangleIndex = 2;
                        ++faceIndex;
                    }
                    return true;
                }
                a = default;
                b = default;
                c = default;
                return false;
            }
        }

        /// <summary>
        /// Computes the inertia of the convex hull.
        /// </summary>
        /// <param name="mass">Mass to scale the inertia tensor with.</param>
        /// <param name="inertia">Inertia of the convex hull.</param>
        public void ComputeInertia(float mass, out BodyInertia inertia)
        {
            var triangleSource = new ConvexHullTriangleSource(this);
            MeshInertiaHelper.ComputeClosedInertia(ref triangleSource, mass, out _, out var inertiaTensor);
            inertia.InverseMass = 1f / mass;
            Symmetric3x3.Invert(inertiaTensor, out inertia.InverseInertiaTensor);
        }

        public ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        {
            return new ConvexShapeBatch<ConvexHull, ConvexHullWide>(pool, initialCapacity);
        }

        public bool RayTest(in RigidPose pose, in Vector3 origin, in Vector3 direction, out float t, out Vector3 normal)
        {
            Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
            var shapeToRay = origin - pose.Position;
            Matrix3x3.TransformTranspose(shapeToRay, orientation, out var localOrigin);
            Matrix3x3.TransformTranspose(direction, orientation, out var localDirection);
            Vector3Wide.Broadcast(localOrigin, out var localOriginBundle);
            Vector3Wide.Broadcast(localDirection, out var localDirectionBundle);

            Helpers.FillVectorWithLaneIndices(out var indexOffsets);
            //The interval of intersection on the ray is the time after it enters all bounding planes, and before it exits any of them.
            //All face normals point outward.
            var latestEntryNumeratorBundle = new Vector<float>(float.MaxValue);
            var latestEntryDenominatorBundle = new Vector<float>(-1);
            var latestEntryIndexBundle = new Vector<int>();
            var earliestExitNumeratorBundle = new Vector<float>(float.MaxValue);
            var earliestExitDenominatorBundle = new Vector<float>(1);
            for (int i = 0; i < BoundingPlanes.Length; ++i)
            {
                ref var boundingPlane = ref BoundingPlanes[i];
                var candidateIndices = new Vector<int>(i << BundleIndexing.VectorShift) + indexOffsets;
                //t = dot(pointOnPlane - origin, planeNormal) / dot(planeNormal, rayDirection)
                //Note that we can defer the division; we don't need to compute the exact t value of *all* planes.
                Vector3Wide.Dot(localOriginBundle, boundingPlane.Normal, out var normalDotOrigin);
                var numerator = boundingPlane.Offset - normalDotOrigin;
                Vector3Wide.Dot(localDirectionBundle, boundingPlane.Normal, out var denominator);
                //A bounding plane is being 'entered' if the ray direction opposes the face normal.
                //Entry denominators are always negative, exit denominators are always positive. Don't have to worry about comparison sign flips.
                //If the denominator is zero, just ignore the lane.
                var useLatestEntryCandidate = Vector.BitwiseAnd(Vector.LessThan(denominator, Vector<float>.Zero), Vector.GreaterThan(numerator * latestEntryDenominatorBundle, latestEntryNumeratorBundle * denominator));
                var useEarliestExitCandidate = Vector.BitwiseAnd(Vector.GreaterThan(denominator, Vector<float>.Zero), Vector.LessThan(numerator * earliestExitDenominatorBundle, earliestExitNumeratorBundle * denominator));
                latestEntryNumeratorBundle = Vector.ConditionalSelect(useLatestEntryCandidate, numerator, latestEntryNumeratorBundle);
                latestEntryDenominatorBundle = Vector.ConditionalSelect(useLatestEntryCandidate, denominator, latestEntryDenominatorBundle);
                latestEntryIndexBundle = Vector.ConditionalSelect(useLatestEntryCandidate, candidateIndices, latestEntryIndexBundle);
                earliestExitNumeratorBundle = Vector.ConditionalSelect(useEarliestExitCandidate, numerator, earliestExitNumeratorBundle);
                earliestExitDenominatorBundle = Vector.ConditionalSelect(useEarliestExitCandidate, denominator, earliestExitDenominatorBundle);
            }
            var latestEntryNumerator = latestEntryNumeratorBundle[0];
            var latestEntryDenominator = latestEntryDenominatorBundle[0];
            var latestEntryIndex = latestEntryIndexBundle[0];
            var earliestExitNumerator = earliestExitNumeratorBundle[0];
            var earliestExitDenominator = earliestExitDenominatorBundle[0];
            for (int i = 1; i < Vector<float>.Count; ++i)
            {
                var latestEntryNumeratorCandidate = latestEntryNumeratorBundle[i];
                var latestEntryDenominatorCandidate = latestEntryDenominatorBundle[i];
                var earliestExitNumeratorCandidate = earliestExitNumeratorBundle[i];
                var earliestExitDenominatorCandidate = earliestExitDenominatorBundle[i];
                if (latestEntryNumeratorCandidate * latestEntryDenominator > latestEntryNumerator * latestEntryDenominatorCandidate)
                {
                    latestEntryNumerator = latestEntryNumeratorCandidate;
                    latestEntryDenominator = latestEntryDenominatorCandidate;
                    latestEntryIndex = latestEntryIndexBundle[i];
                }
                if (earliestExitNumeratorCandidate * earliestExitDenominator < earliestExitNumerator * earliestExitDenominatorCandidate)
                {
                    earliestExitNumerator = earliestExitNumeratorCandidate;
                    earliestExitDenominator = earliestExitDenominatorCandidate;
                }
            }
            //If the earliest exit is behind the origin, there is no hit.
            //If the earliest exit comes before the latest entry, there is no hit.
            //Entry denominators negative, exit denominators positive. Requires comparison sign flip.
            if (earliestExitNumerator < 0 ||
                latestEntryNumerator * earliestExitDenominator < earliestExitNumerator * latestEntryDenominator)
            {
                t = default;
                normal = default;
                return false;
            }
            else
            {
                t = latestEntryNumerator / latestEntryDenominator;
                if (t < 0)
                    t = 0;
                BundleIndexing.GetBundleIndices(latestEntryIndex, out var bundleIndex, out var innerIndex);
                Vector3Wide.ReadSlot(ref BoundingPlanes[bundleIndex].Normal, innerIndex, out normal);
                Matrix3x3.Transform(normal, orientation, out normal);
                return true;
            }

        }
        public void Dispose(BufferPool bufferPool)
        {
            bufferPool.Return(ref Points);
            bufferPool.Return(ref BoundingPlanes);
            bufferPool.Return(ref FaceVertexIndices);
            bufferPool.Return(ref FaceToVertexIndicesStart);
        }


        /// <summary>
        /// Type id of convex hull shapes.
        /// </summary>
        public const int Id = 5;
        public int TypeId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return Id; } }
    }

    public struct ConvexHullWide : IShapeWide<ConvexHull>
    {
        //Unlike the other shapes, single convex hulls are internally vectorized.
        //The "wide" variant is simply a collection of convex hull instances.
        public Buffer<ConvexHull> Hulls;

        public int MinimumWideRayCount => int.MaxValue; //'Wide' ray tests just fall through to scalar tests anyway.

        public bool AllowOffsetMemoryAccess => false;
        public int InternalAllocationSize => Vector<float>.Count * Unsafe.SizeOf<ConvexHull>();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Initialize(in RawBuffer memory)
        {
            Debug.Assert(memory.Length == InternalAllocationSize);
            Hulls = memory.As<ConvexHull>();
        }

        public void Broadcast(in ConvexHull shape)
        {
            for (int i = 0; i < Hulls.Length; ++i)
                Hulls[i] = shape;
        }

        public void GetBounds(ref QuaternionWide orientations, int countInBundle, out Vector<float> maximumRadius, out Vector<float> maximumAngularExpansion, out Vector3Wide min, out Vector3Wide max)
        {
            Vector<float> minimumRadius;
            for (int i = 0; i < countInBundle; ++i)
            {
                Vector3Wide.Broadcast(new Vector3(float.MaxValue), out var minWide);
                Vector3Wide.Broadcast(new Vector3(float.MinValue), out var maxWide);
                QuaternionWide.Rebroadcast(orientations, i, out var orientationWide);
                Matrix3x3Wide.CreateFromQuaternion(orientationWide, out var orientationMatrix);
                Vector<float> maximumRadiusSquaredWide = default;
                Vector<float> minimumRadiusSquaredWide = new Vector<float>(float.MaxValue);
                ref var hull = ref Hulls[i];
                for (int j = 0; j < hull.Points.Length; ++j)
                {
                    ref var localPoint = ref hull.Points[j];
                    Matrix3x3Wide.TransformWithoutOverlap(localPoint, orientationMatrix, out var p);
                    Vector3Wide.LengthSquared(localPoint, out var lengthSquared);
                    maximumRadiusSquaredWide = Vector.Max(lengthSquared, maximumRadiusSquaredWide);
                    minimumRadiusSquaredWide = Vector.Min(lengthSquared, minimumRadiusSquaredWide);
                    Vector3Wide.Min(minWide, p, out minWide);
                    Vector3Wide.Max(maxWide, p, out maxWide);
                }
                Vector3Wide.ReadFirst(minWide, out var minNarrow);
                Vector3Wide.ReadFirst(maxWide, out var maxNarrow);
                float maximumRadiusSquared = maximumRadiusSquaredWide[0];
                float minimumRadiusSquared = minimumRadiusSquaredWide[0];
                for (int j = 1; j < Vector<float>.Count; ++j)
                {
                    //TODO: Check codegen. Bounds checks elided?
                    var minCandidate = new Vector3(minWide.X[j], minWide.Y[j], minWide.Z[j]);
                    var maxCandidate = new Vector3(maxWide.X[j], maxWide.Y[j], maxWide.Z[j]);
                    minNarrow = Vector3.Min(minCandidate, minNarrow);
                    maxNarrow = Vector3.Max(maxCandidate, maxNarrow);

                    var maxRadiusCandidate = maximumRadiusSquaredWide[i];
                    var minRadiusCandidate = minimumRadiusSquaredWide[i];
                    if (maxRadiusCandidate > maximumRadiusSquared)
                        maximumRadiusSquared = maxRadiusCandidate;
                    if (minRadiusCandidate < minimumRadiusSquared)
                        minimumRadiusSquared = minRadiusCandidate;
                }
                GatherScatter.Get(ref maximumRadius, i) = maximumRadiusSquared;
                GatherScatter.Get(ref minimumRadius, i) = minimumRadiusSquared;
                Vector3Wide.WriteSlot(minNarrow, i, ref min);
                Vector3Wide.WriteSlot(maxNarrow, i, ref max);
            }
            minimumRadius = Vector.SquareRoot(minimumRadius);
            maximumRadius = Vector.SquareRoot(maximumRadius);
            maximumAngularExpansion = maximumRadius - minimumRadius;
        }

        public void RayTest(ref RigidPoses poses, ref RayWide rayWide, out Vector<int> intersected, out Vector<float> t, out Vector3Wide normal)
        {
            Debug.Assert(Hulls.Length > 0 && Hulls.Length <= Vector<float>.Count);
            for (int i = 0; i < Hulls.Length; ++i)
            {
                RigidPoses.ReadFirst(GatherScatter.GetOffsetInstance(ref poses, i), out var pose);
                ref var offsetRay = ref GatherScatter.GetOffsetInstance(ref rayWide, i);
                Vector3Wide.ReadFirst(offsetRay.Origin, out var origin);
                Vector3Wide.ReadFirst(offsetRay.Direction, out var direction);
                var intersectedNarrow = Hulls[i].RayTest(pose, origin, direction, out var tNarrow, out var normalNarrow);

                GatherScatter.Get(ref intersected, i) = intersectedNarrow ? -1 : 0;
                GatherScatter.Get(ref t, i) = tNarrow;
                Vector3Wide.WriteSlot(normalNarrow, i, ref normal);
            }
        }

        /// <summary>
        /// Provides an estimate of the scale of a shape. 
        /// </summary>
        /// <param name="terminatedLanes">Mask of lanes which are inactive.</param>
        /// <param name="epsilonScale">Approximate scale of the shape for use in epsilons.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EstimateEpsilonScale(in Vector<int> terminatedLanes, out Vector<float> epsilonScale)
        {
            Vector3Wide bundle;
            for (int i = 0; i < Hulls.Length; ++i)
            {
                if (terminatedLanes[i] < 0)
                    continue;
                Vector3Wide.CopySlot(ref Hulls[i].Points[0], 0, ref bundle, i);
            }
            epsilonScale = (Vector.Abs(bundle.X) + Vector.Abs(bundle.Y) + Vector.Abs(bundle.Z)) * new Vector<float>(1f / 3f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFirst(in ConvexHull source)
        {
            Hulls[0] = source;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSlot(int index, in ConvexHull source)
        {
            Hulls[index] = source;
        }
    }
       
    public struct ConvexHullSupportFinder : ISupportFinder<ConvexHull, ConvexHullWide>
    {
        public bool HasMargin => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ComputeLocalSupport(in ConvexHullWide shape, in Vector3Wide direction, in Vector<int> terminatedLanes, out Vector3Wide support)
        {
            Helpers.FillVectorWithLaneIndices(out var indexOffsets);
            for (int slotIndex = 0; slotIndex < Vector<float>.Count; ++slotIndex)
            {
                if (terminatedLanes[slotIndex] < 0)
                    continue;
                ref var hull = ref shape.Hulls[slotIndex];
                Debug.Assert(hull.Points.Allocated, "If the lane isn't terminated, then the hull should actually exist. Did you forget to create a mask based on the bundle local count?");
                Vector3Wide.Rebroadcast(direction, slotIndex, out var slotDirection);
                var bestIndices = indexOffsets;
                Vector3Wide.Dot(slotDirection, hull.Points[0], out var dot);
                for (int j = 1; j < hull.Points.Length; ++j)
                {
                    ref var candidate = ref hull.Points[j];
                    Vector3Wide.Dot(slotDirection, candidate, out var dotCandidate);
                    var useCandidate = Vector.GreaterThan(dotCandidate, dot);
                    bestIndices = Vector.ConditionalSelect(useCandidate, indexOffsets + new Vector<int>(j << BundleIndexing.VectorShift), bestIndices);
                    dot = Vector.ConditionalSelect(useCandidate, dotCandidate, dot);
                }
                //This horizontal phase is actually a nontrivial cost; platform intrinsics may offer some potential improvements.
                var bestSlotIndex = 0;
                var bestSlotDot = dot[0];
                for (int j = 1; j < Vector<float>.Count; ++j)
                {
                    var candidate = dot[j];
                    if (candidate > bestSlotDot)
                    {
                        bestSlotDot = candidate;
                        bestSlotIndex = j;
                    }
                }
                var supportIndex = bestIndices[bestSlotIndex];
                BundleIndexing.GetBundleIndices(supportIndex, out var bundleIndex, out var innerIndex);
                Vector3Wide.CopySlot(ref hull.Points[bundleIndex], innerIndex, ref support, slotIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ComputeSupport(in ConvexHullWide shape, in Matrix3x3Wide orientation, in Vector3Wide direction, in Vector<int> terminatedLanes, out Vector3Wide support)
        {
            Matrix3x3Wide.TransformByTransposedWithoutOverlap(direction, orientation, out var localDirection);
            ComputeLocalSupport(shape, localDirection, terminatedLanes, out var localSupport);
            Matrix3x3Wide.TransformWithoutOverlap(localSupport, orientation, out support);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetMargin(in ConvexHullWide shape, out Vector<float> margin)
        {
            margin = default;
        }
    }
}
