using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    [System.Serializable]
    public enum CookieAtlasGraphicsFormat
    {
        R11G11B10 = GraphicsFormat.B10G11R11_UFloatPack32,
        R16G16B16A16 = GraphicsFormat.R16G16B16A16_SFloat,
    }

    class LightCookieManager
    {
        HDRenderPipelineAsset m_RenderPipelineAsset = null;

        internal static readonly int s_texSource = Shader.PropertyToID("_SourceTexture");
        internal static readonly int s_sourceMipLevel = Shader.PropertyToID("_SourceMipLevel");
        internal static readonly int s_sourceSize = Shader.PropertyToID("_SourceSize");

        Material m_MaterialFilterAreaLights;
        MaterialPropertyBlock m_MPBFilterAreaLights;

        Material m_CubeToPanoMaterial;

        RenderTexture m_TempRenderTexture0 = null;
        RenderTexture m_TempRenderTexture1 = null;
        
        // Structure for cookies used by directional and spotlights
        PowerOfTwoTextureAtlas m_CookieAtlas;

        // Structure for cookies used by point lights
        TextureCacheCubemap m_CubeCookieTexArray;
        // During the light loop, when reserving space for the cookies (first part of the light loop) the atlas
        // can run out of space, in this case, we set to true this flag which will trigger a re-layouting of the
        // atlas (sort entries by size and insert them again).
        bool                m_2DCookieAtlasNeedsLayouting = false;
        bool                m_NoMoreSpace = false;
        readonly int        cookieAtlasLastValidMip;

        public LightCookieManager(HDRenderPipelineAsset hdAsset, int maxCacheSize)
        {
            // Keep track of the render pipeline asset
            m_RenderPipelineAsset = hdAsset;
            var hdResources = HDRenderPipeline.defaultAsset.renderPipelineResources;

            // Create the texture cookie cache that we shall be using for the area lights
            GlobalLightLoopSettings gLightLoopSettings = hdAsset.currentPlatformRenderPipelineSettings.lightLoopSettings;
            int cookieSize = gLightLoopSettings.cookieAreaTextureArraySize;

            // Also make sure to create the engine material that is used for the filtering
            m_MaterialFilterAreaLights = CoreUtils.CreateEngineMaterial(hdResources.shaders.filterAreaLightCookiesPS);

            int cookieCubeSize = gLightLoopSettings.cubeCookieTexArraySize;
            int cookieAtlasSize = (int)gLightLoopSettings.cookieAtlasSize;
            var cookieFormat = (GraphicsFormat)gLightLoopSettings.cookieAtlasFormat;
            cookieAtlasLastValidMip = gLightLoopSettings.cookieAtlasLastValidMip;

            if (PowerOfTwoTextureAtlas.GetApproxCacheSizeInByte(1, cookieAtlasSize, true, cookieFormat) > HDRenderPipeline.k_MaxCacheSize)
                cookieAtlasSize = PowerOfTwoTextureAtlas.GetMaxCacheSizeForWeightInByte(HDRenderPipeline.k_MaxCacheSize, true, cookieFormat);

            m_CookieAtlas = new PowerOfTwoTextureAtlas(cookieAtlasSize, gLightLoopSettings.cookieAtlasLastValidMip, cookieFormat, name: "Cookie Atlas (Punctual Lights)", useMipMap: true);

            m_CubeToPanoMaterial = CoreUtils.CreateEngineMaterial(hdResources.shaders.cubeToPanoPS);

            m_CubeCookieTexArray = new TextureCacheCubemap("Cookie");
            int cookieCubeResolution = (int)gLightLoopSettings.pointCookieSize;
            if (TextureCacheCubemap.GetApproxCacheSizeInByte(cookieCubeSize, cookieCubeResolution, 1) > HDRenderPipeline.k_MaxCacheSize)
                cookieCubeSize = TextureCacheCubemap.GetMaxCacheSizeForWeightInByte(HDRenderPipeline.k_MaxCacheSize, cookieCubeResolution, 1);

            // For now the cubemap cookie array format is hardcoded to R8G8B8A8 SRGB.
            m_CubeCookieTexArray.AllocTextureArray(cookieCubeSize, cookieCubeResolution, GraphicsFormat.R8G8B8A8_SRGB, true, m_CubeToPanoMaterial);
        }

        public void NewFrame()
        {
            m_CubeCookieTexArray.NewFrame();
            m_CookieAtlas.ResetRequestedTexture();
            m_2DCookieAtlasNeedsLayouting = false;
            m_NoMoreSpace = false;
        }

        public void Release()
        {
            CoreUtils.Destroy(m_MaterialFilterAreaLights);
            CoreUtils.Destroy(m_CubeToPanoMaterial);

            if(m_TempRenderTexture0 != null)
            {
                m_TempRenderTexture0.Release();
                m_TempRenderTexture0 = null;
            }
            if (m_TempRenderTexture1 != null)
            {
                m_TempRenderTexture1.Release();
                m_TempRenderTexture1 = null;
            }

            if (m_CookieAtlas != null)
            {
                m_CookieAtlas.Release();
                m_CookieAtlas = null;
            }
            if (m_CubeCookieTexArray != null)
            {
                m_CubeCookieTexArray.Release();
                m_CubeCookieTexArray = null;
            }
        }

        Texture FilterAreaLightTexture(CommandBuffer cmd, Texture source)
        {
            if (m_MaterialFilterAreaLights == null)
            {
                Debug.LogError("FilterAreaLightTexture has an invalid shader. Can't filter area light cookie.");
                return null;
            }

            int sourceWidth = m_CookieAtlas.AtlasTexture.rt.width;
            int sourceHeight = m_CookieAtlas.AtlasTexture.rt.height;
            int mipMapCount = 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(source.width, source.height), 2));

            if (m_TempRenderTexture0 == null)
            {
                string cacheName = m_CookieAtlas.AtlasTexture.name;
                m_TempRenderTexture0 = new RenderTexture(sourceWidth, sourceHeight, 1, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT0"
                };

                // We start by a horizontal gaussian into mip 1 that reduces the width by a factor 2 but keeps the same height
                m_TempRenderTexture1 = new RenderTexture(sourceWidth >> 1, sourceHeight, 1, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT1"
                };
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.AreaLightCookieConvolution)))
            {
                int targetWidth = sourceWidth;
                int targetHeight = sourceHeight;
                Vector4 targetSize = new Vector4(targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight);

                // Start by copying the source texture to the array slice's mip 0
                {
                    cmd.SetGlobalTexture(s_texSource, source);
                    cmd.SetGlobalInt(s_sourceMipLevel, 0);
                    cmd.SetRenderTarget(m_TempRenderTexture0, 0);
                    cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 0, MeshTopology.Triangles, 3, 1);
                }

                // Then operate on all the remaining mip levels
                Vector4 sourceSize = Vector4.zero;
                for (int mipIndex=1; mipIndex < mipMapCount; mipIndex++)
                {
                    {   // Perform horizontal blur
                        targetWidth = Mathf.Max(1, targetWidth  >> 1);

                        sourceSize.Set(sourceWidth, sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight);
                        targetSize.Set(targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight);

                        cmd.SetGlobalTexture(s_texSource, m_TempRenderTexture0);
                        cmd.SetGlobalInt(s_sourceMipLevel, mipIndex-1);          // Use previous mip as source
                        cmd.SetGlobalVector(s_sourceSize, sourceSize);
                        cmd.SetRenderTarget(m_TempRenderTexture1, mipIndex-1);    // Temp texture is already 1 mip lower than source
                        cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 1, MeshTopology.Triangles, 3, 1);
                    }

                    sourceWidth = targetWidth;

                    {   // Perform vertical blur
                        targetHeight = Mathf.Max(1, targetHeight >> 1);

                        sourceSize.Set(sourceWidth, sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight);
                        targetSize.Set(targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight);

                        cmd.SetGlobalTexture(s_texSource, m_TempRenderTexture1);
                        cmd.SetGlobalInt(s_sourceMipLevel, mipIndex-1);
                        cmd.SetGlobalVector(s_sourceSize, sourceSize);
                        cmd.SetRenderTarget(m_TempRenderTexture0, mipIndex);
                        cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 2, MeshTopology.Triangles, 3, 1);
                    }

                    sourceHeight = targetHeight;
                }
            }

            return m_TempRenderTexture0;
        }

        public void LayoutIfNeeded()
        {
            if (!m_2DCookieAtlasNeedsLayouting)
                return;
            
            if (!m_CookieAtlas.RelayoutEntries())
            {
                Debug.LogError($"No more space in the 2D Cookie Texture Atlas. To solve this issue, increase the size of the cookie atlas in the HDRP settings.");
                m_NoMoreSpace = true;
            }
        }

        public Vector4 Fetch2DCookie(CommandBuffer cmd, Texture cookie)
        {
            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie) && !m_NoMoreSpace)
                Debug.LogError($"2D Light cookie texture {cookie} can't be fetched without having reserved. Check LightLoop.ReserveCookieAtlasTexture");

            if (m_CookieAtlas.NeedsUpdate(cookie))
                m_CookieAtlas.BlitTexture(cmd, scaleBias, cookie, new Vector4(1, 1, 0, 0), blitMips: false);

            return scaleBias;
        }

        public Vector4 FetchAreaCookie(CommandBuffer cmd, Texture cookie)
        {
            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie) && !m_NoMoreSpace)
                Debug.LogError($"Area Light cookie texture {cookie} can't be fetched without having reserved. Check LightLoop.ReserveCookieAtlasTexture");

            if (m_CookieAtlas.NeedsUpdate(cookie))
            {
                // Generate the mips
                Texture filteredAreaLight = FilterAreaLightTexture(cmd, cookie);
                m_CookieAtlas.BlitTexture(cmd, scaleBias, filteredAreaLight, new Vector4(1, 1, 0, 0), blitMips: true, overrideInstanceID: cookie.GetInstanceID());
            }

            return scaleBias;
        }

        public void ReserveSpace(Texture cookie)
        {
            if (cookie == null)
                return;

            if (!m_CookieAtlas.ReserveSpace(cookie))
                m_2DCookieAtlasNeedsLayouting = true;
        }

        public int FetchCubeCookie(CommandBuffer cmd, Texture cookie) => m_CubeCookieTexArray.FetchSlice(cmd, cookie);

        public void ResetAllocator() => m_CookieAtlas.ResetAllocator();

        public void ClearAtlasTexture(CommandBuffer cmd) => m_CookieAtlas.ClearTarget(cmd);

        public RTHandle atlasTexture => m_CookieAtlas.AtlasTexture;
        public Texture cubeCache => m_CubeCookieTexArray.GetTexCache();

        public PowerOfTwoTextureAtlas atlas => m_CookieAtlas;
        public TextureCacheCubemap cubeCookieTexArray => m_CubeCookieTexArray;

        public Vector4 GetCookieAtlasSize()
        {
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                m_CookieAtlas.AtlasTexture.rt.height,
                1.0f / m_CookieAtlas.AtlasTexture.rt.width,
                1.0f / m_CookieAtlas.AtlasTexture.rt.height
           );
        }

        public Vector4 GetCookieAtlasDatas()
        {
            float padding = Mathf.Pow(2.0f, m_CookieAtlas.mipPadding) * 2.0f;
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                padding / (float)m_CookieAtlas.AtlasTexture.rt.width,
                cookieAtlasLastValidMip,
                0
           );
        }
    }
}
