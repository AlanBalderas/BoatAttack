﻿// Buoyancy.cs
// by Alex Zhdankin
// Version 2.1
//
// http://forum.unity3d.com/threads/72974-Buoyancy-script
//
// Terms of use: do whatever you like
//
// Further tweaks by Andre McGrail
//
//

using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;

namespace WaterSystem
{
    public class BuoyantObject : MonoBehaviour
    {
        public BuoyancyType _buoyancyType; // type of buoyancy to calculate
        public float density; // density of the object, this is calculated off it's volume and mass
        public float volume; // volume of the object, this is calculated via it's colliders
        public float voxelResolution = 0.51f; // voxel resolution, represents the half size of a voxel when creating the voxel representation
        private Bounds voxelBounds; // bounds of the voxels
        public Vector3 centerOfMass = Vector3.zero; // Center Of Mass offset
        public float waterLevelOffset = 0f;

        private const float DAMPFER = 0.005f;
        private const float WATER_DENSITY = 1000;

        private float baseDrag; // reference to original drag
        private float baseAngularDrag; // reference to original angular drag
        private int _guid; // GUID for the height system
        private float3 localArchimedesForce;

		[SerializeField]
        private Vector3[] voxels; // voxel position
        private float3[] samplePoints; // sample points for height calc
        private float3[] heights; // water height array(only size of 1 when simple or non-physical)
        private float3[] normals; // water normal array(only used when non-physical and size of 1 also when simple)
        private float3[] velocity; // voxel velocity for buoyancy
        [SerializeField]
        Collider[] colliders; // colliders attatched ot this object
        Rigidbody RB;
        private DebugDrawing[] debugInfo; // For drawing force gizmos
        public float percentSubmerged = 0f;

        [ContextMenu("Initialize")]
		void Init()
        {
            voxels = null;
		    
            if(_buoyancyType == BuoyancyType.NonPhysicalVoxel || _buoyancyType == BuoyancyType.PhysicalVoxel) // If voxel based we need colliders and voxels
            {
                SetupColliders();
                SliceIntoVoxels();
            }

            if (_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel) // If physical, then we need a rigidbody
            {
                // The object must have a RidigBody
                RB = GetComponent<Rigidbody>();
                if (RB == null)
                {
                    RB = gameObject.AddComponent<Rigidbody>();
                    Debug.LogError(string.Format("Buoyancy:Object \"{0}\" had no Rigidbody. Rigidbody has been added.", name));
                }
                RB.centerOfMass = centerOfMass + voxelBounds.center;
                baseDrag = RB.drag;
                baseAngularDrag = RB.angularDrag;
            }

            if (_buoyancyType == BuoyancyType.NonPhysical || _buoyancyType == BuoyancyType.Physical)
            {
                voxels = new Vector3[1];
                voxels[0] = centerOfMass;
            }
            
            velocity = new float3[voxels.Length];
            samplePoints = new float3[voxels.Length];

            float archimedesForceMagnitude = WATER_DENSITY * Mathf.Abs(Physics.gravity.y) * volume;
            localArchimedesForce = new float3(0, archimedesForceMagnitude, 0) / samplePoints.Length;
        }

        private void Start()
        {
            _guid = gameObject.GetInstanceID();

            Init();
            
            debugInfo = new DebugDrawing[voxels.Length];
            heights = new float3[voxels.Length];
            normals = new float3[voxels.Length];
            
            LocalToWorldConversion();
        }

        void SetupColliders()
        {
            // The object must have a Collider
            colliders = GetComponentsInChildren<Collider>();
            if(colliders.Length == 0)
            {
                colliders = new Collider[1];
                colliders[0] = gameObject.AddComponent<BoxCollider>();
                Debug.LogError(string.Format("Buoyancy:Object \"{0}\" had no coll. BoxCollider has been added.", name));
            }
        }

        void Update()
        {
            GerstnerWavesJobs.UpdateSamplePoints(samplePoints, _guid);
            GerstnerWavesJobs.GetData(_guid, ref heights, ref normals);
            
            if(_buoyancyType == BuoyancyType.NonPhysical || _buoyancyType == BuoyancyType.NonPhysicalVoxel) // if acurate the are more points so only heights are needed
            {
                if(_buoyancyType == BuoyancyType.NonPhysical)
                {
                    Vector3 vec  = transform.position;
                    vec.y = heights[0].y + waterLevelOffset;
                    transform.position = vec;
                    transform.up = Vector3.Slerp(transform.up, normals[0], Time.deltaTime);
                }
                else if(_buoyancyType == BuoyancyType.NonPhysicalVoxel)
                {
                    // do the voxel non-physical
                }
            }
            else
            {
                for (int i = 0; i < voxels.Length; i++)
                {
                    velocity[i] = RB.GetPointVelocity(samplePoints[i]);
                }
            }
        }

        private void FixedUpdate()
        {
            if (_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel)
            {
                float submergedAmount = 0f;
                samplePoints = LocalToWorldJob.CompleteJob(_guid);

                if (_buoyancyType == BuoyancyType.PhysicalVoxel)
                {
                    //Debug.Log("new pass: " + gameObject.name);
                    Physics.autoSyncTransforms = false;

                    for (var i = 0; i < voxels.Length; i++)
                        BuoyancyForce(samplePoints[i], velocity[i], heights[i].y + waterLevelOffset, ref submergedAmount, ref debugInfo[i]);
                    Physics.SyncTransforms();
                    Physics.autoSyncTransforms = true;
                    UpdateDrag(submergedAmount);
                }
                else if (_buoyancyType == BuoyancyType.Physical)
                {
                    BuoyancyForce(Vector3.zero, velocity[0], heights[0].y + waterLevelOffset, ref submergedAmount, ref debugInfo[0]);
                    //UpdateDrag(submergedAmount);
                }
            }
        }

        private void LateUpdate()
        {
            LocalToWorldConversion();
        }

        private void OnDisable()
        {
            LocalToWorldJob.Cleanup(_guid);
        }
        
        private void OnDestroy()
        {
            LocalToWorldJob.Cleanup(_guid);
        }

        private void LocalToWorldConversion()
        {
            if (_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel)
            {
                Matrix4x4 transformMatrix = transform.localToWorldMatrix;
                LocalToWorldJob.ScheduleJob(_guid, voxels, transformMatrix);
            }
        }

        private void BuoyancyForce(Vector3 position, float3 velocity, float waterHeight, ref float submergedAmount, ref DebugDrawing _debug)
        {
            _debug.position = position;
            _debug.waterHeight = waterHeight;
            _debug.force = Vector3.zero;

            if (position.y - voxelResolution < waterHeight)
            {
				float k = math.clamp(waterHeight - (position.y - voxelResolution), 0f, 1f);

				submergedAmount += k / voxels.Length;

                var localDampingForce = DAMPFER * RB.mass * -velocity;
                var force = localDampingForce + math.sqrt(k) * localArchimedesForce;
                RB.AddForceAtPosition(force, position);

                _debug.force = force; // For drawing force gizmos
				//Debug.Log(string.Format("Position: {0:f1} -- Force: {1:f2} -- Height: {2:f2}\nVelocty: {3:f2} -- Damp: {4:f2} -- Mass: {5:f1} -- K: {6:f2}", wp, force, waterLevel, velocity, localDampingForce, RB.mass, localArchimedesForce));
			}
		}

        private void UpdateDrag(float submergedAmount)
        {
            percentSubmerged = math.lerp(percentSubmerged, submergedAmount, 0.25f);
            RB.drag = baseDrag + baseDrag * (percentSubmerged * 10f);
            RB.angularDrag = baseAngularDrag + percentSubmerged * 0.5f;
        }

        private void SliceIntoVoxels()
        {
			Quaternion rot = transform.rotation;
            Vector3 pos = transform.position;
            Vector3 size = transform.localScale;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;

            voxels = null;
            var points = new List<Vector3>();

            var rawBounds = VoxelBounds();
            voxelBounds = rawBounds;
            voxelBounds.size = RoundVector(rawBounds.size, voxelResolution);
            for (float ix = -voxelBounds.extents.x; ix < voxelBounds.extents.x; ix += voxelResolution)
            {
                for (float iy = -voxelBounds.extents.y; iy < voxelBounds.extents.y; iy += voxelResolution)
                {
                    for (float iz = -voxelBounds.extents.z; iz < voxelBounds.extents.z; iz += voxelResolution)
                    {
                        float x = (voxelResolution * 0.5f) + ix;
                        float y = (voxelResolution * 0.5f) + iy;
                        float z = (voxelResolution * 0.5f) + iz;

                        var p = new Vector3(x, y, z) + voxelBounds.center;

                        bool inside = false;
                        for(var i = 0; i < colliders.Length; i++)
                        {
                            if (PointIsInsideCollider(colliders[i], p))
                            {
                                inside = true;
                            }
                        }
                        if(inside)
                            points.Add(p);
					}
				}
            }

            voxels = points.ToArray();
			transform.SetPositionAndRotation(pos, rot);
            transform.localScale = size;
            var voxelVolume = Mathf.Pow(voxelResolution, 3f) * voxels.Length;
            var rawVolume = rawBounds.size.x * rawBounds.size.y * rawBounds.size.z;
            volume = Mathf.Min(rawVolume, voxelVolume);
            density = gameObject.GetComponent<Rigidbody>().mass / volume;
        }

		private Bounds VoxelBounds()
		{
            Bounds bounds = new Bounds();
            foreach (Collider nextCollider in colliders)
            {
                bounds.Encapsulate(nextCollider.bounds);
            }
            return bounds;
		}

		static Vector3 RoundVector(Vector3 vec, float rounding)
		{
            return new Vector3(Mathf.Ceil(vec.x / rounding) * rounding, Mathf.Ceil(vec.y / rounding) * rounding, Mathf.Ceil(vec.z / rounding) * rounding);
        }

        private bool PointIsInsideCollider(Collider c, Vector3 p)
        {
            Vector3 cp = Physics.ClosestPoint(p, c, Vector3.zero, UnityEngine.Quaternion.identity);
			return Vector3.Distance(cp, p) < 0.01f ? true : false;
        }

        private void OnDrawGizmosSelected()
        {
			const float gizmoSize = 0.05f;
			var matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);

            if (voxels != null)
            {
                Gizmos.color = Color.yellow;

                foreach (var p in voxels)
                {
                    Gizmos.DrawCube(p, new Vector3(gizmoSize, gizmoSize, gizmoSize));
                }
            }

            Gizmos.matrix = matrix;
            if (voxelResolution >= 0.1f)
			{
                Gizmos.DrawWireCube(voxelBounds.center, voxelBounds.size);
                Vector3 center = voxelBounds.center;
                float y = center.y - voxelBounds.extents.y;
                for (float x = -voxelBounds.extents.x; x < voxelBounds.extents.x; x += voxelResolution)
				{
                    Gizmos.DrawLine(new Vector3(x, y, -voxelBounds.extents.z + center.z), new Vector3(x, y, voxelBounds.extents.z + center.z));
                }
				for (float z = -voxelBounds.extents.z; z < voxelBounds.extents.z; z += voxelResolution)
                {
					Gizmos.DrawLine(new Vector3(-voxelBounds.extents.x, y, z + center.z), new Vector3(voxelBounds.extents.x, y, z + center.z));
                }
            }
			else
                voxelBounds = VoxelBounds();

            Gizmos.color = Color.red;
            Gizmos.DrawSphere(voxelBounds.center + centerOfMass, 0.2f);

            Gizmos.matrix = Matrix4x4.identity;Gizmos.matrix = Matrix4x4.identity;

            if (debugInfo != null)
            {
                foreach (DebugDrawing debug in debugInfo)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawCube(debug.position, new Vector3(gizmoSize, gizmoSize, gizmoSize)); // drawCenter
                    Vector3 water = debug.position;
                    water.y = debug.waterHeight;
                    Gizmos.DrawLine(debug.position, water); // draw the water line
                    Gizmos.DrawSphere(water, gizmoSize * 4f);
                    if(_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel)
                    {
                        Gizmos.color = Color.red;
                        Gizmos.DrawRay(debug.position, debug.force / RB.mass); // draw force
                    }
                }
            }

        }

        struct DebugDrawing
        {
            public Vector3 force;
            public Vector3 position;
            public float waterHeight;
        }

        public enum BuoyancyType
        {
            NonPhysical,
            NonPhysicalVoxel,
            Physical,
            PhysicalVoxel
        }
    }
}