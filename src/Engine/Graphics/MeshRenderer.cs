﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Veldrid;
using Veldrid.Graphics;
using Veldrid.Assets;
using Engine.Assets;
using Newtonsoft.Json;
using System;

namespace Engine.Graphics
{
    public unsafe class MeshRenderer : Component, BoundsRenderItem
    {
        private static readonly string[] s_stages = { "ShadowMap", "Standard" };

        private readonly DynamicDataProvider<Matrix4x4> _worldProvider;
        private readonly DependantDataProvider<Matrix4x4> _inverseTransposeWorldProvider;
        private readonly DynamicDataProvider<TintInfo> _tintInfoProvider;
        private readonly ConstantBufferDataProvider[] _perObjectProviders;
        private int _indexCount;
        private RefOrImmediate<TextureData> _textureRef;
        private RefOrImmediate<MeshData> _meshRef;
        private TextureData _texture;
        private MeshData _mesh;
        private BoundingSphere _centeredBoundingSphere;
        private BoundingBox _centeredBoundingBox;

        private GraphicsSystem _gs;
        private AssetDatabase _ad;

        // Serialization Accessors
        public RefOrImmediate<MeshData> Mesh
        {
            get { return _meshRef; }
            set
            {
                _meshRef = value;
                if (_vb != null)
                {
                    RecreateModel();
                }
            }
        }
        public RefOrImmediate<TextureData> Texture
        {
            get { return _textureRef; }
            set
            {
                _textureRef = value;
                if (_texture != null)
                {
                    RecreateTexture();
                }
            }
        }

        private void RecreateModel()
        {
            _vb.Dispose();
            _ib.Dispose();

            _mesh = _meshRef.Get(_ad);
            _vb = _mesh.CreateVertexBuffer(_gs.Context.ResourceFactory);
            _ib = _mesh.CreateIndexBuffer(_gs.Context.ResourceFactory, out _indexCount);
            _centeredBoundingSphere = _mesh.GetBoundingSphere();
            _centeredBoundingBox = _mesh.GetBoundingBox();
        }

        private void RecreateTexture()
        {
            _deviceTexture.Dispose();
            _textureBinding.Dispose();

            _texture = _textureRef.Get(_ad);
            _deviceTexture = _texture.CreateDeviceTexture(_gs.Context.ResourceFactory);
            _textureBinding = _gs.Context.ResourceFactory.CreateShaderTextureBinding(_deviceTexture);
        }

        // Private device resources -- to be disposed.
        private VertexBuffer _vb;
        private IndexBuffer _ib;
        private DeviceTexture _deviceTexture;
        private ShaderTextureBinding _textureBinding;

        // Shared device resources
        private Material _regularPassMaterial;
        private Material _shadowPassMaterial;

        private static RasterizerState s_wireframeRS;
        private static RasterizerState s_noCullRS;

        public bool Wireframe { get; set; } = false;

        public bool DontCullBackFace { get; set; } = false;

        public TintInfo Tint { get { return _tintInfoProvider.Data; } set { _tintInfoProvider.Data = value; } }

        public Matrix4x4 RenderOffset { get; set; } = Matrix4x4.Identity;

        [JsonIgnore]
        public BoundingBox Bounds
        {
            get
            {
                return BoundingBox.Transform(_centeredBoundingBox, RenderOffset * Transform.GetWorldMatrix());
            }
        }

        public MeshRenderer() : this(EngineEmbeddedAssets.CubeModelID, EngineEmbeddedAssets.PinkTextureID) { }

        [JsonConstructor]
        public MeshRenderer(RefOrImmediate<MeshData> meshData, RefOrImmediate<TextureData> texture)
        {
            _worldProvider = new DynamicDataProvider<Matrix4x4>();
            _inverseTransposeWorldProvider = new DependantDataProvider<Matrix4x4>(_worldProvider, CalculateInverseTranspose);
            _tintInfoProvider = new DynamicDataProvider<TintInfo>();
            _perObjectProviders = new ConstantBufferDataProvider[] { _worldProvider, _inverseTransposeWorldProvider, _tintInfoProvider };
            Mesh = meshData;
            Texture = texture;
        }

        public RenderOrderKey GetRenderOrderKey(Vector3 cameraPosition)
        {
            return RenderOrderKey.Create(Vector3.Distance(Transform.Position, cameraPosition), _regularPassMaterial.GetHashCode());
        }

        public IEnumerable<string> GetStagesParticipated()
        {
            return s_stages;
        }

        public void Render(RenderContext rc, string pipelineStage)
        {
            _worldProvider.Data = RenderOffset * GameObject.Transform.GetWorldMatrix();

            rc.SetVertexBuffer(_vb);
            rc.SetIndexBuffer(_ib);

            if (pipelineStage == "ShadowMap")
            {
                rc.SetMaterial(_shadowPassMaterial);
                _shadowPassMaterial.ApplyPerObjectInput(_worldProvider);
            }
            else
            {
                Debug.Assert(pipelineStage == "Standard");

                rc.SetMaterial(_regularPassMaterial);

                _regularPassMaterial.ApplyPerObjectInputs(_perObjectProviders);
                _regularPassMaterial.UseTexture(0, _textureBinding);
            }

            if (Wireframe)
            {
                rc.SetRasterizerState(s_wireframeRS);
            }
            else if (DontCullBackFace)
            {
                rc.SetRasterizerState(s_noCullRS);
            }
            else
            {
                rc.SetRasterizerState(rc.DefaultRasterizerState);
            }

            rc.DrawIndexedPrimitives(_indexCount, 0);
        }

        protected override void Attached(SystemRegistry registry)
        {
            _gs = registry.GetSystem<GraphicsSystem>();
            _ad = registry.GetSystem<AssetSystem>().Database;
            _texture = Texture.Get(_ad);
            _mesh = Mesh.Get(_ad);
            InitializeContextObjects(_gs.Context, _gs.MaterialCache, _gs.BufferCache);
        }

        protected override void Removed(SystemRegistry registry)
        {
            ClearDeviceResources();
        }

        protected override void OnEnabled()
        {
            _gs.AddRenderItem(this, Transform);
        }

        protected override void OnDisabled()
        {
            _gs.RemoveRenderItem(this);
        }

        private unsafe void InitializeContextObjects(RenderContext context, MaterialCache materialCache, BufferCache bufferCache)
        {
            ResourceFactory factory = context.ResourceFactory;

            Debug.Assert(_vb == null);
            Debug.Assert(_ib == null);
            Debug.Assert(_deviceTexture == null);
            Debug.Assert(_textureBinding == null);

            _vb = bufferCache.GetVertexBuffer(_mesh);
            _ib = bufferCache.GetIndexBuffer(_mesh, out _indexCount);
            _centeredBoundingSphere = _mesh.GetBoundingSphere();
            _centeredBoundingBox = _mesh.GetBoundingBox();

            if (s_regularGlobalInputs == null)
            {
                s_regularGlobalInputs = new MaterialInputs<MaterialGlobalInputElement>(
                    new MaterialGlobalInputElement[]
                    {
                        new MaterialGlobalInputElement("ProjectionMatrixBuffer", MaterialInputType.Matrix4x4, "ProjectionMatrix"),
                        new MaterialGlobalInputElement("ViewMatrixBuffer", MaterialInputType.Matrix4x4, "ViewMatrix"),
                        new MaterialGlobalInputElement("LightProjectionMatrixBuffer", MaterialInputType.Matrix4x4, "LightProjMatrix"),
                        new MaterialGlobalInputElement("LightViewMatrixBuffer", MaterialInputType.Matrix4x4, "LightViewMatrix"),
                        new MaterialGlobalInputElement("LightInfoBuffer", MaterialInputType.Custom, "LightBuffer"),
                        new MaterialGlobalInputElement("CameraInfoBuffer", MaterialInputType.Custom, "CameraInfo"),
                        new MaterialGlobalInputElement("PointLightsBuffer", MaterialInputType.Custom, "PointLights")
                    });
            }

            _regularPassMaterial = materialCache.GetMaterial(
                context,
                RegularPassVertexShaderSource,
                RegularPassFragmentShaderSource,
                s_vertexInputs,
                s_regularGlobalInputs,
                s_perObjectInputs,
                s_textureInputs);

            if (_texture == null)
            {
                _texture = RawTextureDataArray<RgbaFloat>.FromSingleColor(RgbaFloat.Pink);
            }

            _deviceTexture = _texture.CreateDeviceTexture(factory);
            _textureBinding = factory.CreateShaderTextureBinding(_deviceTexture);

            if (s_shadowmapGlobalInputs == null)
            {
                s_shadowmapGlobalInputs = new MaterialInputs<MaterialGlobalInputElement>(
                    new MaterialGlobalInputElement[]
                    {
                        new MaterialGlobalInputElement("ProjectionMatrixBuffer", MaterialInputType.Matrix4x4, "LightProjMatrix"),
                        new MaterialGlobalInputElement("ViewMatrixBuffer", MaterialInputType.Matrix4x4, "LightViewMatrix")
                    });
            }

            _shadowPassMaterial = materialCache.GetMaterial(
                context,
                ShadowMapPassVertexShaderSource,
                ShadowMapPassFragmentShaderSource,
                s_vertexInputs,
                s_shadowmapGlobalInputs,
                s_shadowmapPerObjectInputs,
                MaterialTextureInputs.Empty);

            if (s_wireframeRS == null)
            {
                s_wireframeRS = factory.CreateRasterizerState(FaceCullingMode.None, TriangleFillMode.Wireframe, true, true);
            }
            if (s_noCullRS == null)
            {
                s_noCullRS = factory.CreateRasterizerState(FaceCullingMode.None, TriangleFillMode.Solid, true, true);
            }
        }

        private Matrix4x4 CalculateInverseTranspose(Matrix4x4 m)
        {
            Matrix4x4 inverted;
            Matrix4x4.Invert(m, out inverted);
            return Matrix4x4.Transpose(inverted);
        }

        public void ClearDeviceResources()
        {
            _deviceTexture.Dispose();
            _textureBinding.Dispose();
        }

        public bool Cull(ref BoundingFrustum visibleFrustum)
        {
            Vector3 translation, scale; Quaternion rotation;

            bool decomposed = Matrix4x4.Decompose(RenderOffset, out scale, out rotation, out translation);
            var center = _centeredBoundingSphere.Center + translation + Transform.Position;
            var boundingSphere = new BoundingSphere(center, _centeredBoundingSphere.Radius * (decomposed ? scale.X : 1.0f) * Transform.Scale.X);
            return visibleFrustum.Contains(boundingSphere) == ContainmentType.Disjoint;
        }

        private BoundsRenderItemWireframeRenderer _boundsRenderer;
        private bool _boundsRendererEnabled;

        public void ToggleBoundsRenderer()
        {
            _boundsRendererEnabled = !_boundsRendererEnabled;
            if (_boundsRendererEnabled)
            {
                if (_boundsRenderer == null)
                {
                    _boundsRenderer = new BoundsRenderItemWireframeRenderer(this, _gs.Context);
                }

                _gs.AddRenderItem(_boundsRenderer, Transform);
            }

            else
            {
                if (_boundsRenderer != null)
                {
                    _gs.RemoveRenderItem(_boundsRenderer);
                }
            }
        }

        public bool RayCast(Ray ray, out float distance)
        {
            Matrix4x4 invWorld;
            if (!Matrix4x4.Invert(_worldProvider.Data, out invWorld))
            {
                distance = 0f;
                return false;
            }

            ray = Ray.Transform(ray, invWorld);
            bool result = _mesh.RayCast(ray, out distance);
            if (result)
            {
                Vector3 total = ray.Direction * distance;
                distance = (total * Transform.Scale).Length();
            }

            return result;
        }

        private static readonly string RegularPassVertexShaderSource = "shadow-vertex";
        private static readonly string RegularPassFragmentShaderSource = "shadow-frag";

        private static readonly string ShadowMapPassVertexShaderSource = "shadowmap-vertex";
        private static readonly string ShadowMapPassFragmentShaderSource = "shadowmap-frag";

        private static MaterialVertexInput s_vertexInputs = new MaterialVertexInput(
            VertexPositionNormalTexture.SizeInBytes,
            new MaterialVertexInputElement[]
            {
                new MaterialVertexInputElement("in_position", VertexSemanticType.Position, VertexElementFormat.Float3),
                new MaterialVertexInputElement("in_normal", VertexSemanticType.Normal, VertexElementFormat.Float3),
                new MaterialVertexInputElement("in_texCoord", VertexSemanticType.TextureCoordinate, VertexElementFormat.Float2)
            });
        private static MaterialInputs<MaterialGlobalInputElement> s_regularGlobalInputs;
        private static MaterialInputs<MaterialPerObjectInputElement> s_perObjectInputs = new MaterialInputs<MaterialPerObjectInputElement>(
            new MaterialPerObjectInputElement[]
            {
                new MaterialPerObjectInputElement("WorldMatrixBuffer", MaterialInputType.Matrix4x4, sizeof(Matrix4x4)),
                new MaterialPerObjectInputElement("InverseTransposeWorldMatrixBuffer", MaterialInputType.Matrix4x4, sizeof(Matrix4x4)),
                new MaterialPerObjectInputElement("TintInfoBuffer", MaterialInputType.Float4, sizeof(TintInfo))
            });
        private static MaterialTextureInputs s_textureInputs = new MaterialTextureInputs(
            new MaterialTextureInputElement[]
            {
                new ManualTextureInput("surfaceTexture"),
                new ContextTextureInputElement("ShadowMap")
            });
        private static MaterialInputs<MaterialGlobalInputElement> s_shadowmapGlobalInputs;
        private static MaterialInputs<MaterialPerObjectInputElement> s_shadowmapPerObjectInputs = new MaterialInputs<MaterialPerObjectInputElement>(
            new MaterialPerObjectInputElement[]
            {
                new MaterialPerObjectInputElement("WorldMatrixBuffer", MaterialInputType.Matrix4x4, sizeof(Matrix4x4))
            });
    }
}
