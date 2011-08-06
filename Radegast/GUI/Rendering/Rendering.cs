// 
// Radegast Metaverse Client
// Copyright (c) 2009-2011, Radegast Development Team
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 
//     * Redistributions of source code must retain the above copyright notice,
//       this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of the application "Radegast", nor the names of its
//       contributors may be used to endorse or promote products derived from
//       this software without specific prior CreateReflectionTexture permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// $Id$
//

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Text;
using System.Threading;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenMetaverse;
using OpenMetaverse.Rendering;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
#endregion Usings

namespace Radegast.Rendering
{

    public partial class SceneWindow : RadegastTabControl
    {
        #region Public fields
        /// <summary>
        /// The OpenGL surface
        /// </summary>
        public OpenTK.GLControl glControl = null;

        /// <summary>
        /// Use multi sampling (anti aliasing)
        /// </summary>
        public bool UseMultiSampling = true;

        /// <summary>
        /// Is rendering engine ready and enabled
        /// </summary>
        public bool RenderingEnabled = false;

        /// <summary>
        /// Rednder in wireframe mode
        /// </summary>
        public bool Wireframe = false;

        /// <summary>
        /// Object from up to this distance from us will be rendered
        /// </summary>
        public float DrawDistance
        {
            get { return drawDistance; }
            set
            {
                drawDistance = value;
                drawDistanceSquared = value * value;
                if (Camera != null)
                    Camera.Far = value;
            }
        }

        /// <summary>
        /// List of prims in the scene
        /// </summary>
        Dictionary<uint, RenderPrimitive> Prims = new Dictionary<uint, RenderPrimitive>();
        List<SceneObject> SortedObjects;
        List<SceneObject> OccludedObjects;
        List<RenderAvatar> VisibleAvatars;
        Dictionary<uint, RenderAvatar> Avatars = new Dictionary<uint, RenderAvatar>();

        /// <summary>
        /// Render prims
        /// </summary>
        public bool PrimitiveRenderingEnabled = true;

        /// <summary>
        /// Render avatars
        /// </summary>
        public bool AvatarRenderingEnabled = true;

        /// <summary>
        /// Show avatar skeloton
        /// </summary>
        public bool RenderAvatarSkeleton = false;

        /// <summary>
        /// Cache images after jpeg2000 decode. Uses a lot of disk space and can cause disk trashing
        /// </summary>
        public bool CacheDecodedTextures = false;

        #endregion Public fields

        #region Private fields

        Camera Camera;
        SceneObject trackedObject;
        Vector3 lastTrackedObjectPos = RHelp.InvalidPosition;
        RenderAvatar myself;
        bool cameraLocked = false;

        Dictionary<UUID, TextureInfo> TexturesPtrMap = new Dictionary<UUID, TextureInfo>();
        MeshmerizerR renderer;
        OpenTK.Graphics.GraphicsMode GLMode = null;
        AutoResetEvent TextureThreadContextReady = new AutoResetEvent(false);

        delegate void GenericTask();
        BlockingQueue<GenericTask> PendingTasks = new BlockingQueue<GenericTask>();
        Thread genericTaskThread;

        BlockingQueue<TextureLoadItem> PendingTextures = new BlockingQueue<TextureLoadItem>();

        Font HoverTextFont = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Regular);
        Font AvatarTagFont = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);
        Dictionary<UUID, Bitmap> sculptCache = new Dictionary<UUID, Bitmap>();
        OpenTK.Matrix4 ModelMatrix;
        OpenTK.Matrix4 ProjectionMatrix;
        int[] Viewport = new int[4];
        System.Diagnostics.Stopwatch renderTimer;
        float lastFrameTime = 0f;
        float advTimerTick = 0f;
        float minLODFactor = 0.0001f;

        float[] sunPos = new float[] { 128f, 128f, 5000f, 1f };
        float ambient = 0.26f;
        float difuse = 0.27f;
        float specular = 0.20f;
        OpenTK.Vector4 ambientColor;
        OpenTK.Vector4 difuseColor;
        OpenTK.Vector4 specularColor;
        float drawDistance = 48f;
        float drawDistanceSquared = 48f * 48f;

        GridClient Client;
        RadegastInstance Instance;

        #endregion Private fields

        #region Construction and disposal
        public SceneWindow(RadegastInstance instance)
            : base(instance)
        {
            InitializeComponent();
            Disposed += new EventHandler(frmPrimWorkshop_Disposed);

            this.Instance = instance;
            this.Client = instance.Client;

            UseMultiSampling = cbAA.Checked = instance.GlobalSettings["use_multi_sampling"];
            cbAA.CheckedChanged += cbAA_CheckedChanged;

            this.instance = instance;

            genericTaskThread = new Thread(new ThreadStart(GenericTaskRunner));
            genericTaskThread.IsBackground = true;
            genericTaskThread.Name = "Generic task queue";
            genericTaskThread.Start();

            renderer = new MeshmerizerR();
            renderTimer = new System.Diagnostics.Stopwatch();
            renderTimer.Start();

            // Camera initial setting
            Camera = new Camera();
            InitCamera();
            SetWaterPlanes();

            tbDrawDistance.Value = (int)DrawDistance;
            lblDrawDistance.Text = string.Format("Draw distance: {0}", tbDrawDistance.Value);

            Client.Objects.TerseObjectUpdate += new EventHandler<TerseObjectUpdateEventArgs>(Objects_TerseObjectUpdate);
            Client.Objects.ObjectUpdate += new EventHandler<PrimEventArgs>(Objects_ObjectUpdate);
            Client.Objects.ObjectDataBlockUpdate += new EventHandler<ObjectDataBlockUpdateEventArgs>(Objects_ObjectDataBlockUpdate);
            Client.Objects.KillObject += new EventHandler<KillObjectEventArgs>(Objects_KillObject);
            Client.Network.SimChanged += new EventHandler<SimChangedEventArgs>(Network_SimChanged);
            Client.Terrain.LandPatchReceived += new EventHandler<LandPatchReceivedEventArgs>(Terrain_LandPatchReceived);
            Client.Avatars.AvatarAnimation += new EventHandler<AvatarAnimationEventArgs>(AvatarAnimationChanged);
            Client.Avatars.AvatarAppearance += new EventHandler<AvatarAppearanceEventArgs>(Avatars_AvatarAppearance);
            Client.Appearance.AppearanceSet += new EventHandler<AppearanceSetEventArgs>(Appearance_AppearanceSet);
            Instance.Netcom.ClientDisconnected += new EventHandler<DisconnectedEventArgs>(Netcom_ClientDisconnected);
            Application.Idle += new EventHandler(Application_Idle);
        }

        void frmPrimWorkshop_Disposed(object sender, EventArgs e)
        {
            RenderingEnabled = false;
            Application.Idle -= new EventHandler(Application_Idle);

            PendingTextures.Close();

            Client.Objects.TerseObjectUpdate -= new EventHandler<TerseObjectUpdateEventArgs>(Objects_TerseObjectUpdate);
            Client.Objects.ObjectUpdate -= new EventHandler<PrimEventArgs>(Objects_ObjectUpdate);
            Client.Objects.ObjectDataBlockUpdate -= new EventHandler<ObjectDataBlockUpdateEventArgs>(Objects_ObjectDataBlockUpdate);
            Client.Objects.KillObject -= new EventHandler<KillObjectEventArgs>(Objects_KillObject);
            Client.Network.SimChanged -= new EventHandler<SimChangedEventArgs>(Network_SimChanged);
            Client.Terrain.LandPatchReceived -= new EventHandler<LandPatchReceivedEventArgs>(Terrain_LandPatchReceived);
            Client.Avatars.AvatarAnimation -= new EventHandler<AvatarAnimationEventArgs>(AvatarAnimationChanged);
            Client.Avatars.AvatarAppearance -= new EventHandler<AvatarAppearanceEventArgs>(Avatars_AvatarAppearance);
            Client.Appearance.AppearanceSet -= new EventHandler<AppearanceSetEventArgs>(Appearance_AppearanceSet);

            PendingTasks.Close();
            if (genericTaskThread != null)
            {
                genericTaskThread.Join(2000);
                genericTaskThread = null;
            }

            if (instance.Netcom != null)
            {
                Instance.Netcom.ClientDisconnected -= new EventHandler<DisconnectedEventArgs>(Netcom_ClientDisconnected);
            }

            if (glControl != null)
            {
                glControl.Dispose();
            }
            glControl = null;

            lock (sculptCache)
            {
                foreach (var img in sculptCache.Values)
                    img.Dispose();
                sculptCache.Clear();
            }

            lock (Prims) Prims.Clear();
            lock (Avatars) Avatars.Clear();

            TexturesPtrMap.Clear();
            GC.Collect();
        }

        void Application_Idle(object sender, EventArgs e)
        {
            if (glControl != null && !glControl.IsDisposed && RenderingEnabled)
            {
                try
                {
                    while (glControl != null && glControl.IsIdle && RenderingEnabled)
                    {
                        MainRenderLoop();
                        if (instance.MonoRuntime)
                        {
                            Application.DoEvents();
                        }
                    }
                }
                catch (ObjectDisposedException)
                { }
            }
        }
        #endregion Construction and disposal

        #region Tab Events
        public void RegisterTabEvents()
        {
            this.RadegastTab.TabAttached += new EventHandler(RadegastTab_TabAttached);
            this.RadegastTab.TabDetached += new EventHandler(RadegastTab_TabDetached);
            this.RadegastTab.TabClosed += new EventHandler(RadegastTab_TabClosed);
        }

        public void UnregisterTabEvents()
        {
            this.RadegastTab.TabAttached -= new EventHandler(RadegastTab_TabAttached);
            this.RadegastTab.TabDetached -= new EventHandler(RadegastTab_TabDetached);
            this.RadegastTab.TabClosed -= new EventHandler(RadegastTab_TabClosed);
        }

        void RadegastTab_TabDetached(object sender, EventArgs e)
        {
            instance.GlobalSettings["scene_window_docked"] = false;
            pnlDebug.Visible = true;
        }

        void RadegastTab_TabAttached(object sender, EventArgs e)
        {
            instance.GlobalSettings["scene_window_docked"] = true;
            pnlDebug.Visible = false;
        }

        void RadegastTab_TabClosed(object sender, EventArgs e)
        {
            if (this.RadegastTab != null)
            {
                UnregisterTabEvents();
            }
        }

        #endregion Tab Events

        #region Network messaage handlers
        void Terrain_LandPatchReceived(object sender, LandPatchReceivedEventArgs e)
        {
            if (e.Simulator.Handle == Client.Network.CurrentSim.Handle)
            {
                terrainModified = true;
            }
        }

        void Netcom_ClientDisconnected(object sender, DisconnectedEventArgs e)
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated || !instance.MonoRuntime)
                {
                    BeginInvoke(new MethodInvoker(() => Netcom_ClientDisconnected(sender, e)));
                }
                return;
            }

            Dispose();
        }

        void Network_SimChanged(object sender, SimChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(() => Network_SimChanged(sender, e)));
                return;
            }

            ResetTerrain();
            lock (sculptCache)
            {
                foreach (var img in sculptCache.Values)
                    img.Dispose();
                sculptCache.Clear();
            }
            lock (Prims) Prims.Clear();
            lock (Avatars) Avatars.Clear();
            SetWaterPlanes();
            LoadCurrentPrims();
            InitCamera();
        }

        void Objects_KillObject(object sender, KillObjectEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => Objects_KillObject(sender, e)));
                }
                return;
            }

            // TODO: there should be really cleanup of resources when removing prims and avatars
            lock (Prims) Prims.Remove(e.ObjectLocalID);
            lock (Avatars) Avatars.Remove(e.ObjectLocalID);
        }

        void Objects_TerseObjectUpdate(object sender, TerseObjectUpdateEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            if (e.Prim.ID == Client.Self.AgentID)
            {
                trackedObject = myself;
            }

            //If it is an avatar, we don't need to deal with the terse update stuff, unless it sends textures to us
            if (e.Prim.PrimData.PCode == PCode.Avatar && e.Update.Textures == null)
                return;

            UpdatePrimBlocking(e.Prim);
        }

        void Objects_ObjectUpdate(object sender, PrimEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            UpdatePrimBlocking(e.Prim);
        }

        void Objects_ObjectDataBlockUpdate(object sender, ObjectDataBlockUpdateEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            UpdatePrimBlocking(e.Prim);
        }

        void AvatarAnimationChanged(object sender, AvatarAnimationEventArgs e)
        {
            // We don't currently have UUID -> RenderAvatar mapping so we need to walk the list
            foreach (RenderAvatar av in Avatars.Values)
            {
                if (av.avatar.ID == e.AvatarID)
                {
                    av.glavatar.skel.flushanimations();
                    foreach (Animation anim in e.Animations)
                    {

                        UUID tid = UUID.Random();
                        skeleton.mAnimationTransactions.Add(tid, av);

                        BinBVHAnimationReader bvh;
                        if (skeleton.mAnimationCache.TryGetValue(anim.AnimationID, out bvh))
                        {
                            skeleton.addanimation(null, tid, bvh);
                            continue;
                        }

                        Logger.Log("Requesting new animation asset " + anim.AnimationID.ToString(), Helpers.LogLevel.Info);

                        Client.Assets.RequestAsset(anim.AnimationID, AssetType.Animation, false, SourceType.Asset, tid, animRecievedCallback);
                    }
                    break;
                }
            }
        }

        void animRecievedCallback(AssetDownload transfer, Asset asset)
        {
            if (transfer.Success)
            {
                skeleton.addanimation(asset, transfer.ID, null);
            }
        }

        void Avatars_AvatarAppearance(object sender, AvatarAppearanceEventArgs e)
        {
            // We don't currently have UUID -> RenderAvatar mapping so we need to walk the list
            foreach (RenderAvatar av in Avatars.Values)
            {
                if (av.avatar.ID == e.AvatarID)
                {
                    av.glavatar.morph(av.avatar);
                }
            }
        }

        void Appearance_AppearanceSet(object sender, AppearanceSetEventArgs e)
        {
            if (e.Success)
            {
                RenderAvatar me;
                if (Avatars.TryGetValue(Client.Self.LocalID, out me))
                {
                    me.glavatar.morph(me.avatar);
                }
            }
        }

        #endregion Network messaage handlers

        #region glControl setup and disposal
        public void SetupGLControl()
        {
            RenderingEnabled = false;

            if (glControl != null)
                glControl.Dispose();
            glControl = null;

            GLMode = null;

            try
            {
                if (!UseMultiSampling)
                {
                    GLMode = new OpenTK.Graphics.GraphicsMode(OpenTK.DisplayDevice.Default.BitsPerPixel, 24, 8, 0);
                }
                else
                {
                    for (int aa = 0; aa <= 4; aa += 2)
                    {
                        var testMode = new OpenTK.Graphics.GraphicsMode(OpenTK.DisplayDevice.Default.BitsPerPixel, 24, 8, aa);
                        if (testMode.Samples == aa)
                        {
                            GLMode = testMode;
                        }
                    }
                }
            }
            catch
            {
                GLMode = null;
            }


            try
            {
                if (GLMode == null)
                {
                    // Try default mode
                    glControl = new OpenTK.GLControl();
                }
                else
                {
                    glControl = new OpenTK.GLControl(GLMode);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message, Helpers.LogLevel.Warning, Client);
                glControl = null;
            }

            if (glControl == null)
            {
                Logger.Log("Failed to initialize OpenGL control, cannot continue", Helpers.LogLevel.Error, Client);
                return;
            }

            Logger.Log("Initializing OpenGL mode: " + GLMode.ToString(), Helpers.LogLevel.Info);

            glControl.Paint += glControl_Paint;
            glControl.Resize += glControl_Resize;
            glControl.MouseDown += glControl_MouseDown;
            glControl.MouseUp += glControl_MouseUp;
            glControl.MouseMove += glControl_MouseMove;
            glControl.MouseWheel += glControl_MouseWheel;
            glControl.Load += new EventHandler(glControl_Load);
            glControl.Disposed += new EventHandler(glControl_Disposed);
            glControl.Dock = DockStyle.Fill;
            glControl.VSync = false;
            Controls.Add(glControl);
            glControl.BringToFront();
        }

        void glControl_Disposed(object sender, EventArgs e)
        {
            TextureThreadRunning = false;
            PendingTextures.Close();
            glControl.Paint -= glControl_Paint;
            glControl.Resize -= glControl_Resize;
            glControl.MouseDown -= glControl_MouseDown;
            glControl.MouseUp -= glControl_MouseUp;
            glControl.MouseMove -= glControl_MouseMove;
            glControl.MouseWheel -= glControl_MouseWheel;
            glControl.Load -= new EventHandler(glControl_Load);
            glControl.Disposed -= glControl_Disposed;
        }

        void SetSun()
        {
            ambientColor = new OpenTK.Vector4(ambient, ambient, ambient, difuse);
            difuseColor = new OpenTK.Vector4(difuse, difuse, difuse, difuse);
            specularColor = new OpenTK.Vector4(specular, specular, specular, specular);
            GL.Light(LightName.Light0, LightParameter.Ambient, ambientColor);
            GL.Light(LightName.Light0, LightParameter.Diffuse, difuseColor);
            GL.Light(LightName.Light0, LightParameter.Specular, specularColor);
            GL.Light(LightName.Light0, LightParameter.Position, sunPos);
        }

        void glControl_Load(object sender, EventArgs e)
        {
            try
            {
                GL.ShadeModel(ShadingModel.Smooth);

                GL.Enable(EnableCap.Lighting);
                GL.Enable(EnableCap.Light0);
                SetSun();

                GL.ClearDepth(1.0d);
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(CullFaceMode.Back);

                // GL.Color() tracks objects ambient and diffuse color
                GL.Enable(EnableCap.ColorMaterial);
                GL.ColorMaterial(MaterialFace.Front, ColorMaterialParameter.AmbientAndDiffuse);

                GL.DepthMask(true);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.Hint(HintTarget.PerspectiveCorrectionHint, HintMode.Nicest);
                GL.MatrixMode(MatrixMode.Projection);

                GL.AlphaFunc(AlphaFunction.Greater, 0.5f);
                GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
                string glExtensions = GL.GetString(StringName.Extensions);
                RenderSettings.HasMipmap = glExtensions.Contains("GL_SGIS_generate_mipmap");
                RenderSettings.UseVBO = glExtensions.Contains("ARB_vertex_buffer_object");
                RenderSettings.HasShaders = glExtensions.Contains("vertex_shader") && glExtensions.Contains("fragment_shader");

                // Double check if we have mipmap ability
                if (RenderSettings.HasMipmap)
                {
                    try
                    {
                        int testID = -1;
                        Bitmap testPic = new Bitmap(1, 1);
                        BitmapData testData = testPic.LockBits(new Rectangle(0, 0, 1, 1), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        GL.GenTextures(1, out testID);
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, 1, 1, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgr, PixelType.UnsignedByte, testData.Scan0);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.GenerateMipmap, 1);
                        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                        testPic.UnlockBits(testData);
                        testPic.Dispose();
                        GL.DeleteTexture(testID);
                    }
                    catch
                    {
                        Logger.DebugLog("Don't have glGenerateMipmap() after all");
                        RenderSettings.HasMipmap = false;
                    }
                }

                RenderingEnabled = true;
                // Call the resizing function which sets up the GL drawing window
                // and will also invalidate the GL control
                glControl_Resize(null, null);
                RenderingEnabled = false;

                glControl.Context.MakeCurrent(null);
                TextureThreadContextReady.Reset();
                var textureThread = new Thread(() => TextureThread())
                {
                    IsBackground = true,
                    Name = "TextureDecodingThread"
                };
                textureThread.Start();
                TextureThreadContextReady.WaitOne(1000, false);
                glControl.MakeCurrent();
                InitWater();
                RenderingEnabled = true;
                LoadCurrentPrims();
            }
            catch (Exception ex)
            {
                RenderingEnabled = false;
                Logger.Log("Failed to initialize OpenGL control", Helpers.LogLevel.Warning, Client, ex);
            }
        }

        #endregion glControl setup and disposal

        #region glControl paint and resize events
        private void MainRenderLoop()
        {
            if (!RenderingEnabled) return;
            lastFrameTime = (float)renderTimer.Elapsed.TotalSeconds;

            // Something went horribly wrong
            if (lastFrameTime < 0) return;

            // Stopwatch loses resolution if it runs for a long time, reset it
            renderTimer.Reset();
            renderTimer.Start();

            // Determine if we need to throttle frame rate
            bool throttle = false;

            // Some other app has focus
            if (Form.ActiveForm == null)
            {
                throttle = true;
            }
            else
            {
                // If we're docked but not active tab, throttle
                if (!this.RadegastTab.Selected && !this.RadegastTab.Detached)
                {
                    throttle = true;
                }
            }

            // Limit FPS to max 15
            if (throttle)
            {
                int msToSleep = 66 - ((int)(lastFrameTime / 1000));
                if (msToSleep < 10) msToSleep = 10;
                Thread.Sleep(msToSleep);
            }

            Render(false);

            glControl.SwapBuffers();
        }

        void glControl_Paint(object sender, EventArgs e)
        {
            MainRenderLoop();
        }

        private void glControl_Resize(object sender, EventArgs e)
        {
            if (!RenderingEnabled) return;
            glControl.MakeCurrent();

            GL.ClearColor(0.39f, 0.58f, 0.93f, 1.0f);

            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.PushMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();

            SetPerspective();

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();
        }
        #endregion glControl paint and resize events

        #region Mouse handling
        bool dragging = false;
        int dragX, dragY, downX, downY;

        private void glControl_MouseWheel(object sender, MouseEventArgs e)
        {
            Camera.MoveToTarget(e.Delta / -500f);
        }

        SceneObject RightclickedObject;
        int RightclickedFaceID;

        private void glControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                downX = dragX = e.X;
                downY = dragY = e.Y;
                if (ModifierKeys == Keys.None)
                {
                    object picked;
                    int LeftclickedFaceID;
                    if (TryPick(e.X, e.Y, out picked, out LeftclickedFaceID))
                    {
                        if (picked is RenderPrimitive)
                        {
                            TryTouchObject((RenderPrimitive)picked);
                        }
                    }
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                object picked;
                RightclickedObject = null;
                if (TryPick(e.X, e.Y, out picked, out RightclickedFaceID))
                {
                    if (picked is SceneObject)
                    {
                        RightclickedObject = (SceneObject)picked;
                    }
                }
                ctxMenu.Show(glControl, e.X, e.Y);
            }
        }

        private RenderPrimitive m_currentlyTouchingObject = null;
        private void TryTouchObject(RenderPrimitive LeftclickedObject)
        {
            if ((LeftclickedObject.Prim.Flags & PrimFlags.Touch) != 0)
            {
                if (m_currentlyTouchingObject != null)
                {
                    if (m_currentlyTouchingObject.Prim.LocalID != LeftclickedObject.Prim.LocalID)
                    {
                        //Changed what we are touching... stop touching the old one
                        TryEndTouchObject();

                        //Then set the new one and touch it for the first time
                        m_currentlyTouchingObject = LeftclickedObject;
                        Client.Self.Grab(LeftclickedObject.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, RightclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                        Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero);
                    }
                    else
                        Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero);
                }
                else
                {
                    m_currentlyTouchingObject = LeftclickedObject;
                    Client.Self.Grab(LeftclickedObject.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, RightclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                    Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero);
                }
            }
        }

        private void TryEndTouchObject()
        {
            if (m_currentlyTouchingObject != null)
                Client.Self.DeGrab(m_currentlyTouchingObject.Prim.LocalID);
            m_currentlyTouchingObject = null;
        }

        private void glControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                int deltaX = e.X - dragX;
                int deltaY = e.Y - dragY;
                float pixelToM = 1f / 75f;
                if (e.Button == MouseButtons.Left)
                {
                    if (ModifierKeys == Keys.None)
                    {
                        //Only touch if we arn't doing anything else
                        object picked;
                        int LeftclickedFaceID;
                        if (TryPick(e.X, e.Y, out picked, out LeftclickedFaceID))
                        {
                            if (picked is RenderPrimitive)
                                TryTouchObject((RenderPrimitive)picked);
                        }
                        else
                            TryEndTouchObject();
                    }

                    // Pan
                    if (ModifierKeys == Keys.Control || ModifierKeys == (Keys.Alt | Keys.Control | Keys.Shift))
                    {
                        Camera.Pan(deltaX * pixelToM * 2, deltaY * pixelToM * 2);
                    }

                    // Alt-zoom (up down move camera closer to target, left right rotate around target)
                    if (ModifierKeys == Keys.Alt)
                    {
                        Camera.MoveToTarget(deltaY * pixelToM);
                        Camera.Rotate(-deltaX * pixelToM, true);
                    }

                    // Rotate camera in a vertical circle around target on up down mouse movement
                    if (ModifierKeys == (Keys.Alt | Keys.Control))
                    {
                        Camera.Rotate(deltaY * pixelToM, false);
                        Camera.Rotate(-deltaX * pixelToM, true);
                    }
                }

                dragX = e.X;
                dragY = e.Y;
            }
        }

        private void glControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                object clicked;
                int faceID;
                if (ModifierKeys == Keys.None)
                    TryEndTouchObject();//Stop touching no matter whether we are touching anything
                if (e.X == downX && e.Y == downY) // click
                {
                    if (TryPick(e.X, e.Y, out clicked, out faceID))
                    {
                        if (ModifierKeys == Keys.Alt && clicked is Vector3)
                        {
                            Camera.FocalPoint = (Vector3)clicked;
                            Cursor.Position = glControl.PointToScreen(new Point(glControl.Width / 2, glControl.Height / 2));
                        }
                        else if (clicked is RenderPrimitive)
                        {
                            RenderPrimitive picked = (RenderPrimitive)clicked;
                            if (ModifierKeys == Keys.Alt)
                            {
                                Camera.FocalPoint = picked.RenderPosition;
                                Cursor.Position = glControl.PointToScreen(new Point(glControl.Width / 2, glControl.Height / 2));
                            }
                        }
                        else if (clicked is RenderAvatar)
                        {
                            RenderAvatar av = (RenderAvatar)clicked;
                            if (ModifierKeys == Keys.Alt)
                            {
                                Vector3 pos = av.RenderPosition;
                                pos.Z += 1.5f; // focus roughly on the chest area
                                Camera.FocalPoint = pos;
                                Cursor.Position = glControl.PointToScreen(new Point(glControl.Width / 2, glControl.Height / 2));
                            }
                        }
                    }
                }
            }
        }
        #endregion Mouse handling

        // Switch to ortho display mode for drawing hud
        public void GLHUDBegin()
        {
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.Light0);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -5, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        // Switch back to frustrum display mode
        public void GLHUDEnd()
        {
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Lighting);
            GL.Enable(EnableCap.Light0);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
        }

        public int GLLoadImage(Bitmap bitmap, bool hasAlpha)
        {
            int ret = -1;
            GL.GenTextures(1, out ret);
            GL.BindTexture(TextureTarget.Texture2D, ret);

            Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

            BitmapData bitmapData =
                bitmap.LockBits(
                rectangle,
                ImageLockMode.ReadOnly,
                hasAlpha ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                hasAlpha ? PixelInternalFormat.Rgba : PixelInternalFormat.Rgb8,
                bitmap.Width,
                bitmap.Height,
                0,
                hasAlpha ? OpenTK.Graphics.OpenGL.PixelFormat.Bgra : OpenTK.Graphics.OpenGL.PixelFormat.Bgr,
                PixelType.UnsignedByte,
                bitmapData.Scan0);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            if (RenderSettings.HasMipmap)
            {
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.GenerateMipmap, 1);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            }
            else
            {
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            }

            bitmap.UnlockBits(bitmapData);
            return ret;
        }

        #region Texture thread
        bool TextureThreadRunning = true;

        void TextureThread()
        {
            OpenTK.INativeWindow window = new OpenTK.NativeWindow();
            OpenTK.Graphics.IGraphicsContext context = new OpenTK.Graphics.GraphicsContext(GLMode, window.WindowInfo);
            context.MakeCurrent(window.WindowInfo);
            TextureThreadContextReady.Set();
            PendingTextures.Open();
            Logger.DebugLog("Started Texture Thread");

            while (window.Exists && TextureThreadRunning)
            {
                window.ProcessEvents();

                TextureLoadItem item = null;

                if (!PendingTextures.Dequeue(Timeout.Infinite, ref item)) continue;

                // Already have this one loaded
                if (item.Data.TextureInfo.TexturePointer != 0) continue;

                byte[] imageBytes = null;
                if (item.TGAData != null)
                {
                    imageBytes = item.TGAData;
                }
                else if (item.TextureData != null || item.LoadAssetFromCache)
                {
                    if (item.LoadAssetFromCache)
                    {
                        item.TextureData = Client.Assets.Cache.GetCachedAssetBytes(item.Data.TextureInfo.TextureID);
                    }
                    ManagedImage mi;
                    if (!OpenJPEG.DecodeToImage(item.TextureData, out mi)) continue;

                    bool hasAlpha = false;
                    bool fullAlpha = false;
                    bool isMask = false;
                    if ((mi.Channels & ManagedImage.ImageChannels.Alpha) != 0)
                    {
                        fullAlpha = true;
                        isMask = true;

                        // Do we really have alpha, is it all full alpha, or is it a mask
                        for (int i = 0; i < mi.Alpha.Length; i++)
                        {
                            if (mi.Alpha[i] < 255)
                            {
                                hasAlpha = true;
                            }
                            if (mi.Alpha[i] != 0)
                            {
                                fullAlpha = false;
                            }
                            if (mi.Alpha[i] != 0 && mi.Alpha[i] != 255)
                            {
                                isMask = false;
                            }
                        }

                        if (!hasAlpha)
                        {
                            mi.ConvertChannels(mi.Channels & ~ManagedImage.ImageChannels.Alpha);
                        }
                    }

                    item.Data.TextureInfo.HasAlpha = hasAlpha;
                    item.Data.TextureInfo.FullAlpha = fullAlpha;
                    item.Data.TextureInfo.IsMask = isMask;

                    imageBytes = mi.ExportTGA();
                    if (CacheDecodedTextures)
                    {
                        RHelp.SaveCachedImage(imageBytes, item.TeFace.TextureID, hasAlpha, fullAlpha, isMask);
                    }
                }

                if (imageBytes != null)
                {
                    Image img;

                    using (MemoryStream byteData = new MemoryStream(imageBytes))
                    {
                        img = OpenMetaverse.Imaging.LoadTGAClass.LoadTGA(byteData);
                    }

                    Bitmap bitmap = (Bitmap)img;

                    bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    item.Data.TextureInfo.TexturePointer = GLLoadImage(bitmap, item.Data.TextureInfo.HasAlpha);
                    GL.Flush();
                    bitmap.Dispose();
                }

                item.TextureData = null;
                item.TGAData = null;
                imageBytes = null;
            }
            context.Dispose();
            window.Dispose();
            Logger.DebugLog("Texture thread exited");
        }
        #endregion Texture thread

        void GenericTaskRunner()
        {
            PendingTasks.Open();
            Logger.DebugLog("Started generic task thread");

            while (true)
            {
                GenericTask task = null;
                if (!PendingTasks.Dequeue(Timeout.Infinite, ref task)) break;
                task.Invoke();
            }
            Logger.DebugLog("Generic task thread exited");
        }

        void LoadCurrentPrims()
        {
            if (!Client.Network.Connected) return;

            ThreadPool.QueueUserWorkItem(sync =>
            {
                if (PrimitiveRenderingEnabled)
                {
                    List<Primitive> mainPrims = Client.Network.CurrentSim.ObjectsPrimitives.FindAll((Primitive root) => root.ParentID == 0);
                    foreach (Primitive mainPrim in mainPrims)
                    {
                        UpdatePrimBlocking(mainPrim);
                        Client.Network.CurrentSim.ObjectsPrimitives
                            .FindAll((Primitive child) => child.ParentID == mainPrim.LocalID)
                            .ForEach((Primitive subPrim) => UpdatePrimBlocking(subPrim));
                    }
                }

                if (AvatarRenderingEnabled)
                {
                    List<Avatar> avis = Client.Network.CurrentSim.ObjectsAvatars.FindAll((Avatar a) => true);
                    foreach (Avatar avatar in avis)
                    {
                        UpdatePrimBlocking(avatar);
                        Client.Network.CurrentSim.ObjectsPrimitives
                            .FindAll((Primitive child) => child.ParentID == avatar.LocalID)
                            .ForEach((Primitive attachedPrim) =>
                            {
                                UpdatePrimBlocking(attachedPrim);
                                Client.Network.CurrentSim.ObjectsPrimitives
                                    .FindAll((Primitive child) => child.ParentID == attachedPrim.LocalID)
                                    .ForEach((Primitive attachedPrimChild) =>
                                    {
                                        UpdatePrimBlocking(attachedPrimChild);
                                    });
                            });
                    }
                }
            });
        }

        private void ControlLoaded(object sender, EventArgs e)
        {
            ThreadPool.QueueUserWorkItem(sync =>
            {
                InitAvatarData();
                AvatarDataInitialzied();
            });
        }

        #region Private methods (the meat)

        private void AvatarDataInitialzied()
        {
            if (IsDisposed) return;

            // Ensure that this is done on the main thread
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => AvatarDataInitialzied()));
                return;
            }

            foreach (VisualParamEx vpe in VisualParamEx.morphParams.Values)
            {
                comboBox_morph.Items.Add(vpe.Name);
            }

            foreach (VisualParamEx vpe in VisualParamEx.drivenParams.Values)
            {
                comboBox_driver.Items.Add(vpe.Name);
            }

            SetupGLControl();
        }

        private void InitAvatarData()
        {
            GLAvatar.loadlindenmeshes2("avatar_lad.xml");
        }

        private void UpdateCamera()
        {
            if (Client != null)
            {
                Client.Self.Movement.Camera.LookAt(Camera.Position, Camera.FocalPoint);
                Client.Self.Movement.Camera.Far = Camera.Far = DrawDistance;
            }
        }

        void InitCamera()
        {
            Vector3 camPos = Client.Self.SimPosition + new Vector3(-4, 0, 1) * Client.Self.Movement.BodyRotation;
            camPos.Z += 2f;
            Camera.Position = camPos;
            Camera.FocalPoint = Client.Self.SimPosition + new Vector3(5, 0, 0) * Client.Self.Movement.BodyRotation;
            Camera.Zoom = 1.0f;
            Camera.Far = DrawDistance;
        }

        Vector3 PrimPos(Primitive prim)
        {
            Vector3 pos;
            Quaternion rot;
            PrimPosAndRot(GetSceneObject(prim.LocalID), out pos, out rot);
            return pos;
        }

        bool IsAttached(uint parentLocalID)
        {
            if (parentLocalID == 0) return false;
            if (Client.Network.CurrentSim.ObjectsAvatars.ContainsKey(parentLocalID))
            {
                return true;
            }
            else
            {
                return IsAttached(Client.Network.CurrentSim.ObjectsPrimitives[parentLocalID].ParentID);
            }
        }

        SceneObject GetSceneObject(uint localID)
        {
            RenderPrimitive parent;
            RenderAvatar avi;
            if (Prims.TryGetValue(localID, out parent))
            {
                return parent;
            }
            else if (Avatars.TryGetValue(localID, out avi))
            {
                return avi;
            }
            return null;
        }

        /// <summary>
        /// Calculates finar rendering position for objects on the scene
        /// </summary>
        /// <param name="obj">SceneObject whose position is calculated</param>
        /// <param name="pos">Rendering position</param>
        /// <param name="rot">Rendering rotation</param>
        void PrimPosAndRot(SceneObject obj, out Vector3 pos, out Quaternion rot)
        {
            // Sanity check
            if (obj == null)
            {
                pos = RHelp.InvalidPosition;
                rot = Quaternion.Identity;
                return;
            }

            if (obj.BasePrim.ParentID == 0)
            {
                // We are the root prim, return our interpolated position
                pos = obj.InterpolatedPosition;
                rot = obj.InterpolatedRotation;
                return;
            }
            else
            {
                pos = RHelp.InvalidPosition;
                rot = Quaternion.Identity;

                // Not root, find our parent
                SceneObject p = GetSceneObject(obj.BasePrim.ParentID);
                if (p == null) return;

                // If we don't know parent position, recursively find out
                if (!p.PositionCalculated)
                {
                    PrimPosAndRot(p, out p.RenderPosition, out p.RenderRotation);
                    p.DistanceSquared = FindClosestDistanceSquared(Camera.RenderPosition, p);
                    p.PositionCalculated = true;
                }

                Vector3 parentPos = p.RenderPosition;
                Quaternion parentRot = p.RenderRotation;

                if (p is RenderPrimitive)
                {
                    // Child prim (our parent is another prim here)
                    pos = parentPos + obj.InterpolatedPosition * parentRot;
                    rot = parentRot * obj.InterpolatedRotation;
                }
                else if (p is RenderAvatar)
                {
                    // Calculating position and rotation of the root prim of an attachment here
                    // (our parent is an avatar here)
                    RenderAvatar parentav = (RenderAvatar)p;

                    // Check for invalid attachment point
                    int attachment_index = (int)obj.BasePrim.PrimData.AttachmentPoint;
                    if (attachment_index > GLAvatar.attachment_points.Count()) return;
                    attachment_point apoint = GLAvatar.attachment_points[attachment_index];
                    skeleton skel = parentav.glavatar.skel;
                    if (!skel.mBones.ContainsKey(apoint.joint)) return;

                    // Bone position and rotation
                    Bone bone = skel.mBones[apoint.joint];
                    Vector3 bpos = bone.getTotalOffset();
                    Quaternion brot = bone.getTotalRotation();

                    // Start with avatar positon
                    pos = parentPos;
                    rot = parentRot;

                    // Translate and rotate to the joint calculated position
                    pos += bpos * rot;
                    rot *= brot;

                    // Translate and rotate built in joint offset
                    pos += apoint.position * rot;
                    rot *= apoint.rotation;

                    // Translate and rotate from the offset from the attachment point
                    // set in teh appearance editor
                    pos += obj.BasePrim.Position * rot;
                    rot *= obj.BasePrim.Rotation;

                    pos -= parentav.glavatar.skel.getOffset("mPelvis");
                }
                return;
            }
        }

        /// <summary>
        /// Finds the closest distance between the given pos and an object
        /// (Assumes that the object is a box slightly)
        /// </summary>
        /// <param name="vector3"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        private float FindClosestDistanceSquared(Vector3 calcPos, SceneObject p)
        {
            if (p.BoundingVolume == null || !RenderSettings.HeavierDistanceChecking)
                return Vector3.DistanceSquared(calcPos, p.RenderPosition);

            Vector3 posToCheckFrom = Vector3.Zero;
            //Get the bounding boxes for this prim
            Vector3 boundingBoxMin = p.RenderPosition + p.BoundingVolume.ScaledMin;
            Vector3 boundingBoxMax = p.RenderPosition + p.BoundingVolume.ScaledMax;
            if (calcPos.X > boundingBoxMin.X &&
                    calcPos.X < boundingBoxMax.X)
            {
                //Between the two
                posToCheckFrom.X = calcPos.X;
            }
            else
            {
                float minX = Abs(boundingBoxMin.X - calcPos.X);
                float maxX = Abs(boundingBoxMax.X - calcPos.X);
                if (maxX > minX)
                {
                    //The min is closer in the X direction
                    posToCheckFrom.X = boundingBoxMin.X;
                }
                else
                {
                    //The max is closer in the X direction
                    posToCheckFrom.X = boundingBoxMax.X;
                }
            }

            if (calcPos.Y > boundingBoxMin.Y &&
                    calcPos.Y < boundingBoxMax.Y)
            {
                //Between the two
                posToCheckFrom.Y = calcPos.Y;
            }
            else
            {
                float minY = Abs(boundingBoxMin.Y - calcPos.Y);
                float maxY = Abs(boundingBoxMax.Y - calcPos.Y);
                if (maxY > minY)
                {
                    //The min is closer in the Y direction
                    posToCheckFrom.Y = boundingBoxMin.Y;
                }
                else
                {
                    //The max is closer in the Y direction
                    posToCheckFrom.Y = boundingBoxMax.Y;
                }
            }

            if (calcPos.Z > boundingBoxMin.Z &&
                    calcPos.Z < boundingBoxMax.Z)
            {
                //Between the two
                posToCheckFrom.Z = calcPos.Z;
            }
            else
            {
                float minZ = Abs(boundingBoxMin.Z - calcPos.Z);
                float maxZ = Abs(boundingBoxMax.Z - calcPos.Z);
                if (maxZ > minZ)
                {
                    //The min is closer in the Z direction
                    posToCheckFrom.Z = boundingBoxMin.Z;
                }
                else
                {
                    //The max is closer in the Z direction
                    posToCheckFrom.Z = boundingBoxMax.Z;
                }
            }
            return Vector3.DistanceSquared(calcPos, posToCheckFrom);
        }

        private float Abs(float p)
        {
            if (p < 0)
                p *= -1;
            return p;
        }

        private void SetPerspective()
        {
            float dAspRat = (float)glControl.Width / (float)glControl.Height;
            GluPerspective(50.0f * Camera.Zoom, dAspRat, 0.1f, 1000f);
        }


#pragma warning disable 0612
        OpenTK.Graphics.TextPrinter Printer = new OpenTK.Graphics.TextPrinter(OpenTK.Graphics.TextQuality.High);
#pragma warning restore 0612

        private void RenderStats()
        {
            // This is a FIR filter known as a MMA or Modified Mean Average, using a 20 point sampling width
            advTimerTick = ((19 * advTimerTick) + lastFrameTime) / 20;
            // Stats in window title for now
            Text = String.Format("Scene Viewer: FPS {0:000.00} Texture decode queue: {1}, Sculpt queue: {2}",
                1d / advTimerTick,
                PendingTextures.Count,
                PendingTasks.Count);

#if TURNS_OUT_PRINTER_IS_EXPENISVE
            int posX = glControl.Width - 100;
            int posY = 0;

            Printer.Begin();
            Printer.Print(String.Format("FPS {0:000.00}", 1d / advTimerTick), AvatarTagFont, Color.Orange,
                new RectangleF(posX, posY, 100, 50),
                OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);
            Printer.End();
#endif
        }

        private void RenderText(RenderPass pass)
        {
            lock (Avatars)
            {
                GL.Color4(0f, 0f, 0f, 0.4f);
                foreach (RenderAvatar av in VisibleAvatars)
                {
                    Vector3 avPos = av.RenderPosition;
                    if (av.DistanceSquared > 400f) continue;

                    byte[] faceColor = null;

                    OpenTK.Vector3 tagPos = RHelp.TKVector3(avPos);
                    tagPos.Z += 2.2f;
                    OpenTK.Vector3 screenPos;
                    if (!Math3D.GluProject(tagPos, ModelMatrix, ProjectionMatrix, Viewport, out screenPos)) continue;

                    string tagText = instance.Names.Get(av.avatar.ID, av.avatar.Name);
                    if (!string.IsNullOrEmpty(av.avatar.GroupName))
                        tagText = av.avatar.GroupName + "\n" + tagText;

                    var tSize = Printer.Measure(tagText, AvatarTagFont);
                    if (pass == RenderPass.Picking)
                    {
                        //Send avatar anyway, we're attached to it
                        int faceID = 0;
                        foreach (FaceData f in av.data)
                        {
                            if (f != null)
                            {
                                byte[] primNrBytes = Utils.Int16ToBytes((short)f.PickingID);
                                faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceID, 254 };
                                GL.Color4(faceColor);
                                break;
                            }
                            faceID++;
                        }
                    }
                    // Render tag backround
                    GL.Begin(BeginMode.Quads);
                    float halfWidth = tSize.BoundingBox.Width / 2 + 12;
                    float halfHeight = tSize.BoundingBox.Height / 2 + 5;
                    GL.Vertex2(screenPos.X - halfWidth, screenPos.Y - halfHeight);
                    GL.Vertex2(screenPos.X + halfWidth, screenPos.Y - halfHeight);
                    GL.Vertex2(screenPos.X + halfWidth, screenPos.Y + halfHeight);
                    GL.Vertex2(screenPos.X - halfWidth, screenPos.Y + halfHeight);
                    GL.End();

                    screenPos.Y = glControl.Height - screenPos.Y;
                    screenPos.X -= tSize.BoundingBox.Width / 2;
                    screenPos.Y -= tSize.BoundingBox.Height / 2 + 2;

                    if (screenPos.Y > 0)
                    {
                        Printer.Begin();
                        Color textColor = pass == RenderPass.Simple ?
                            Color.Orange :
                            Color.FromArgb(faceColor[3], faceColor[0], faceColor[1], faceColor[2]);
                        Printer.Print(tagText, AvatarTagFont, textColor,
                            new RectangleF(screenPos.X, screenPos.Y, tSize.BoundingBox.Width + 2, tSize.BoundingBox.Height + 2),
                            OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);
                        Printer.End();
                    }
                }
            }

            lock (SortedObjects)
            {
                int primNr = 0;
                foreach (SceneObject obj in SortedObjects)
                {
                    if (!(obj is RenderPrimitive)) continue;

                    RenderPrimitive prim = (RenderPrimitive)obj;
                    primNr++;

                    if (!string.IsNullOrEmpty(prim.BasePrim.Text))
                    {
                        string text = System.Text.RegularExpressions.Regex.Replace(prim.BasePrim.Text, "(\r?\n)+", "\n");
                        OpenTK.Vector3 primPos = RHelp.TKVector3(prim.RenderPosition);

                        // Display hovertext only on objects that are withing 12m of the camera
                        if (prim.DistanceSquared > (12 * 12)) continue;

                        primPos.Z += prim.BasePrim.Scale.Z * 0.8f;

                        // Convert objects world position to 2D screen position in pixels
                        OpenTK.Vector3 screenPos;
                        if (!Math3D.GluProject(primPos, ModelMatrix, ProjectionMatrix, Viewport, out screenPos)) continue;
                        screenPos.Y = glControl.Height - screenPos.Y;

                        Printer.Begin();

                        Color color = Color.FromArgb((int)(prim.BasePrim.TextColor.A * 255), (int)(prim.BasePrim.TextColor.R * 255), (int)(prim.BasePrim.TextColor.G * 255), (int)(prim.BasePrim.TextColor.B * 255));

                        var size = Printer.Measure(text, HoverTextFont);
                        screenPos.X -= size.BoundingBox.Width / 2;
                        screenPos.Y -= size.BoundingBox.Height;

                        if (screenPos.Y > 0)
                        {
                            if (pass == RenderPass.Picking)
                            {
                                //Send the prim anyway, we're attached to it
                                int faceID = 0;
                                foreach (Face f in prim.Faces)
                                {
                                    if (f.UserData != null)
                                    {
                                        byte[] primNrBytes = Utils.Int16ToBytes((short)((FaceData)f.UserData).PickingID);
                                        byte[] faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceID, 255 };
                                        Printer.Print(text, HoverTextFont, Color.FromArgb(faceColor[3], faceColor[0], faceColor[1], faceColor[2]), new RectangleF(screenPos.X, screenPos.Y, size.BoundingBox.Width + 2, size.BoundingBox.Height + 2), OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);
                                        break;
                                    }
                                    faceID++;
                                }
                            }
                            else
                            {
                                // Shadow
                                if (color != Color.Black)
                                    Printer.Print(text, HoverTextFont, Color.Black, new RectangleF(screenPos.X + 1, screenPos.Y + 1, size.BoundingBox.Width + 2, size.BoundingBox.Height + 2), OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);

                                // Text
                                Printer.Print(text, HoverTextFont, color, new RectangleF(screenPos.X, screenPos.Y, size.BoundingBox.Width + 2, size.BoundingBox.Height + 2), OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);
                            }
                        }

                        Printer.End();
                    }
                }
            }
        }

        #region avatars

        private void AddAvatarToScene(Avatar av)
        {
            lock (Avatars)
            {
                if (Avatars.ContainsKey(av.LocalID))
                {
                    // flag we got an update??
                    updateAVtes(Avatars[av.LocalID]);
                }
                else
                {
                    GLAvatar ga = new GLAvatar();

                    //ga.morph(av);
                    RenderAvatar ra = new Rendering.RenderAvatar();
                    ra.avatar = av;
                    ra.glavatar = ga;
                    updateAVtes(ra);
                    Avatars.Add(av.LocalID, ra);
                    ra.glavatar.morph(av);
                    if (av.LocalID == Client.Self.LocalID)
                    {
                        myself = ra;
                    }
                }
            }
        }

        private void updateAVtes(RenderAvatar ra)
        {
            if (ra.avatar.Textures == null)
                return;

            int[] tes = { 8, 9, 10, 11, 19, 20 };

            foreach (int fi in tes)
            {
                Primitive.TextureEntryFace TEF = ra.avatar.Textures.FaceTextures[fi];
                if (TEF == null)
                    continue;

                if (ra.data[fi] == null || ra.data[fi].TextureInfo.TextureID != TEF.TextureID)
                {
                    FaceData data = new FaceData();
                    ra.data[fi] = data;
                    data.TextureInfo.TextureID = TEF.TextureID;

                    DownloadTexture(new TextureLoadItem()
                    {
                        Data = data,
                        Prim = ra.avatar,
                        TeFace = ra.avatar.Textures.FaceTextures[fi]
                    });
                }
            }
        }

        private void RenderAvatarsSkeleton(RenderPass pass)
        {
            if (!RenderAvatarSkeleton) return;

            lock (Avatars)
            {
                foreach (RenderAvatar av in Avatars.Values)
                {
                    // Individual prim matrix
                    GL.PushMatrix();

                    // Prim roation and position
                    Vector3 pos = av.avatar.Position;
           
                    Vector3 avataroffset = av.glavatar.skel.getOffset("mPelvis");

                    GL.MultMatrix(Math3D.CreateSRTMatrix(new Vector3(1, 1, 1), av.avatar.Rotation, pos - avataroffset));

                    GL.Begin(BeginMode.Lines);

                    GL.Color3(1.0, 0.0, 0.0);

                    foreach (Bone b in av.glavatar.skel.mBones.Values)
                    {
                        Vector3 newpos = b.getTotalOffset();

                        if (b.parent != null)
                        {
                            Vector3 parentpos = b.parent.getTotalOffset();
                            GL.Vertex3(parentpos.X, parentpos.Y, parentpos.Z);
                        }
                        else
                        {
                            GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        }

                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        //Mark the joints


                        newpos.X += 0.01f;
                        newpos.Y += 0.01f;
                        newpos.Z += 0.01f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Z -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.01f;
                        newpos.Z += 0.01f;
                        newpos.X -= 0.01f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);



                    }



                    GL.Color3(0.0, 1.0, 0.0);

                    GL.End();

                    GL.PopMatrix();
                }
            }
        }

        private void RenderAvatars(RenderPass pass)
        {
            if (!AvatarRenderingEnabled) return;

            lock (Avatars)
            {
                GL.EnableClientState(ArrayCap.VertexArray);
                GL.EnableClientState(ArrayCap.TextureCoordArray);
                GL.EnableClientState(ArrayCap.NormalArray);

                int avatarNr = 0;
                foreach (RenderAvatar av in VisibleAvatars)
                {
                    avatarNr++;

                    // Whole avatar position
                    GL.PushMatrix();

                    Vector3 avataroffset = av.glavatar.skel.getOffset("mPelvis");
                          
                    // Prim roation and position
                    GL.MultMatrix(Math3D.CreateSRTMatrix(Vector3.One, av.RenderRotation, av.RenderPosition - avataroffset));

                    if (av.glavatar._meshes.Count > 0)
                    {
                        int faceNr = 0;
                        foreach (GLMesh mesh in av.glavatar._meshes.Values)
                        {
                            if (av.glavatar.skel.mNeedsMeshRebuild)
                            {
                                mesh.applyjointweights();
                            }

                            faceNr++;
                            if (!av.glavatar._showSkirt && mesh.Name == "skirtMesh")
                                continue;

                            if (mesh.Name == "hairMesh") // Don't render the hair mesh for the moment
                                continue;

                            GL.Color3(1f, 1f, 1f);

                            // Individual part mesh matrix
                            GL.PushMatrix();

                            // Special case for eyeballs we need to offset the mesh to the correct position
                            // We have manually added the eyeball offset based on the headbone when we
                            // constructed the meshes, but why are the position offsets we got when loading
                            // the other meshes <0,7,0> ?
                            if (mesh.Name == "eyeBallLeftMesh")
                            {
                                // Mesh roation and position
                                GL.MultMatrix(Math3D.CreateSRTMatrix(Vector3.One, av.glavatar.skel.mLeftEye.getTotalRotation(), av.glavatar.skel.mLeftEye.getTotalOffset()));
                            }
                            if (mesh.Name == "eyeBallRightMesh")
                            {
                                // Mesh roation and position
                                GL.MultMatrix(Math3D.CreateSRTMatrix(Vector3.One, av.glavatar.skel.mRightEye.getTotalRotation(), av.glavatar.skel.mRightEye.getTotalOffset()));
                            }

                            //Should we be offsetting the base meshs at all?
                            //if (mesh.Name == "headMesh")
                            //{
                            //    GL.MultMatrix(Math3D.CreateTranslationMatrix(av.glavatar.skel.getDeltaOffset("mHead")));
                            //}


                            if (pass == RenderPass.Picking)
                            {
                                GL.Disable(EnableCap.Texture2D);

                                for (int i = 0; i < av.data.Length; i++)
                                {
                                    if (av.data[i] != null)
                                    {
                                        av.data[i].PickingID = avatarNr;
                                    }
                                }
                                byte[] primNrBytes = Utils.Int16ToBytes((short)avatarNr);
                                byte[] faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceNr, 254 };
                                GL.Color4(faceColor);
                            }
                            else
                            {
                                if (av.data[mesh.teFaceID] == null)
                                {
                                    GL.Disable(EnableCap.Texture2D);
                                }
                                else
                                {
                                    if (mesh.teFaceID != 0)
                                    {
                                        GL.Enable(EnableCap.Texture2D);
                                        GL.BindTexture(TextureTarget.Texture2D, av.data[mesh.teFaceID].TextureInfo.TexturePointer);
                                    }
                                    else
                                    {
                                        GL.Disable(EnableCap.Texture2D);
                                    }
                                }
                            }

                            GL.TexCoordPointer(2, TexCoordPointerType.Float, 0, mesh.RenderData.TexCoords);
                            GL.VertexPointer(3, VertexPointerType.Float, 0, mesh.RenderData.Vertices);
                            GL.NormalPointer(NormalPointerType.Float, 0, mesh.RenderData.Normals);

                            GL.DrawElements(BeginMode.Triangles, mesh.RenderData.Indices.Length, DrawElementsType.UnsignedShort, mesh.RenderData.Indices);

                            GL.BindTexture(TextureTarget.Texture2D, 0);

                            GL.PopMatrix();

                        }

                        av.glavatar.skel.mNeedsMeshRebuild = false;
                    }

                    // Whole avatar position
                    GL.PopMatrix();
                }

                GL.Disable(EnableCap.Texture2D);
                GL.DisableClientState(ArrayCap.NormalArray);
                GL.DisableClientState(ArrayCap.VertexArray);
                GL.DisableClientState(ArrayCap.TextureCoordArray);

            }
        }
        #endregion avatars

        #region Keyboard
        private float upKeyHeld = 0;
        private bool isHoldingHome = false;
        ///<summary>
        ///The time before we fly instead of trying to jump (in seconds)
        ///</summary>
        private const float upKeyHeldBeforeFly = 0.5f;

        void CheckKeyboard(float time)
        {
            if (ModifierKeys == Keys.None)
            {
                // Movement forwards and backwards and body rotation
                Client.Self.Movement.AtPos = Instance.Keyboard.IsKeyDown(Keys.Up);
                Client.Self.Movement.AtNeg = Instance.Keyboard.IsKeyDown(Keys.Down);
                Client.Self.Movement.TurnLeft = Instance.Keyboard.IsKeyDown(Keys.Left);
                Client.Self.Movement.TurnRight = Instance.Keyboard.IsKeyDown(Keys.Right);

                if (Client.Self.Movement.Fly)
                {
                    //Find whether we are going up or down
                    Client.Self.Movement.UpPos = Instance.Keyboard.IsKeyDown(Keys.PageUp);
                    Client.Self.Movement.UpNeg = Instance.Keyboard.IsKeyDown(Keys.PageDown);
                    //The nudge positions are required to land (at least Neg is, unclear whether we should send Pos)
                    Client.Self.Movement.NudgeUpPos = Client.Self.Movement.UpPos;
                    Client.Self.Movement.NudgeUpNeg = Client.Self.Movement.UpNeg;
                    if (Client.Self.Velocity.Z > 0 && Client.Self.Movement.UpNeg)//HACK: Sometimes, stop fly fails
                        Client.Self.Fly(false);//We've hit something, stop flying
                }
                else
                {
                    //Don't send the nudge pos flags, we don't need them
                    Client.Self.Movement.NudgeUpPos = false;
                    Client.Self.Movement.NudgeUpNeg = false;
                    Client.Self.Movement.UpPos = Instance.Keyboard.IsKeyDown(Keys.PageUp);
                    Client.Self.Movement.UpNeg = Instance.Keyboard.IsKeyDown(Keys.PageDown);
                }
                if (Instance.Keyboard.IsKeyDown(Keys.Home))//Flip fly settings
                {
                    //Holding the home key only makes it change once, 
                    // not flip over and over, so keep track of it
                    if (!isHoldingHome)
                    {
                        Client.Self.Movement.Fly = !Client.Self.Movement.Fly;
                        isHoldingHome = true;
                    }
                }
                else
                    isHoldingHome = false;

                if (!Client.Self.Movement.Fly &&
                    Instance.Keyboard.IsKeyDown(Keys.PageUp))
                {
                    upKeyHeld += time;
                    if (upKeyHeld > upKeyHeldBeforeFly)//Wait for a bit before we fly, they may be trying to jump
                        Client.Self.Movement.Fly = true;
                }
                else
                    upKeyHeld = 0;//Reset the count


                if (Client.Self.Movement.TurnLeft)
                {
                    Client.Self.Movement.BodyRotation = Client.Self.Movement.BodyRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, time);
                }
                else if (client.Self.Movement.TurnRight)
                {
                    Client.Self.Movement.BodyRotation = Client.Self.Movement.BodyRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -time);
                }

                if (Instance.Keyboard.IsKeyDown(Keys.Escape))
                {
                    InitCamera();
                }
            }
            else if (ModifierKeys == Keys.Shift)
            {
                // Strafe
                Client.Self.Movement.LeftNeg = Instance.Keyboard.IsKeyDown(Keys.Right);
                Client.Self.Movement.LeftPos = Instance.Keyboard.IsKeyDown(Keys.Left);
            }
            else if (ModifierKeys == Keys.Alt)
            {
                // Camera horizontal rotation
                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Rotate(-time, true);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Rotate(time, true);
                } // Camera vertical rotation
                else if (Instance.Keyboard.IsKeyDown(Keys.PageDown))
                {
                    Camera.Rotate(-time, false);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.PageUp))
                {
                    Camera.Rotate(time, false);
                } // Camera zoom
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.MoveToTarget(time);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.MoveToTarget(-time);
                }
            }
            else if (ModifierKeys == (Keys.Alt | Keys.Control))
            {
                // Camera horizontal rotation
                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Rotate(-time, true);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Rotate(time, true);
                } // Camera vertical rotation
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.Rotate(-time, false);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.Rotate(time, false);
                }
            }
            else if (ModifierKeys == Keys.Control)
            {
                // Camera pan
                float timeFactor = 3f;

                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Pan(time * timeFactor, 0f);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Pan(-time * timeFactor, 0f);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.Pan(0f, time * timeFactor);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.Pan(0f, -time * timeFactor);
                }
            }
        }
        #endregion Keyboard

        #region Terrain
        bool terrainModified = true;
        float[,] heightTable = new float[256, 256];
        Face terrainFace;
        ushort[] terrainIndices;
        ColorVertex[] terrainVertices;
        int terrainTexture = -1;
        bool fetchingTerrainTexture = false;
        Bitmap terrainImage = null;
        int terrainVBO = -1;
        int terrainIndexVBO = -1;
        bool terrainInProgress = false;
        bool terrainTextureNeedsUpdate = false;
        float terrainTimeSinceUpdate = RenderSettings.MinimumTimeBetweenTerrainUpdated + 1f; // Update terrain om first run

        private void ResetTerrain()
        {
            ResetTerrain(true);
        }

        private void ResetTerrain(bool removeImage)
        {
            if (terrainImage != null)
            {
                terrainImage.Dispose();
                terrainImage = null;
            }

            if (terrainVBO != -1)
            {
                GL.DeleteBuffers(1, ref terrainVBO);
                terrainVBO = -1;
            }

            if (terrainIndexVBO != -1)
            {
                GL.DeleteBuffers(1, ref terrainIndexVBO);
                terrainIndexVBO = -1;
            }

            if (removeImage)
            {
                if (terrainTexture != -1)
                {
                    GL.DeleteTexture(terrainTexture);
                    terrainTexture = -1;
                }
            }

            fetchingTerrainTexture = false;
            terrainModified = true;
        }

        private void UpdateTerrain()
        {
            if (Client.Network.CurrentSim == null || Client.Network.CurrentSim.Terrain == null) return;

            ThreadPool.QueueUserWorkItem(sync =>
            {
                int step = 1;

                for (int x = 0; x < 255; x += step)
                {
                    for (int y = 0; y < 255; y += step)
                    {
                        float z = 0;
                        int patchNr = ((int)x / 16) * 16 + (int)y / 16;
                        if (Client.Network.CurrentSim.Terrain[patchNr] != null
                            && Client.Network.CurrentSim.Terrain[patchNr].Data != null)
                        {
                            float[] data = Client.Network.CurrentSim.Terrain[patchNr].Data;
                            z = data[(int)x % 16 * 16 + (int)y % 16];
                        }
                        heightTable[x, y] = z;
                    }
                }

                terrainFace = renderer.TerrainMesh(heightTable, 0f, 255f, 0f, 255f);
                terrainVertices = new ColorVertex[terrainFace.Vertices.Count];
                for (int i = 0; i < terrainFace.Vertices.Count; i++)
                {
                    byte[] part = Utils.IntToBytes(i);
                    terrainVertices[i] = new ColorVertex()
                    {
                        Vertex = terrainFace.Vertices[i],
                        Color = new Color4b()
                        {
                            R = part[0],
                            G = part[1],
                            B = part[2],
                            A = 253 // terrain picking
                        }
                    };
                }
                terrainIndices = terrainFace.Indices.ToArray();
                terrainInProgress = false;
                terrainModified = false;
                terrainTextureNeedsUpdate = true;
                terrainTimeSinceUpdate = 0f;
            });
        }

        void UpdateTerrainTexture()
        {
            if (!fetchingTerrainTexture)
            {
                fetchingTerrainTexture = true;
                ThreadPool.QueueUserWorkItem(sync =>
                {
                    Simulator sim = Client.Network.CurrentSim;
                    terrainImage = TerrainSplat.Splat(instance, heightTable,
                        new UUID[] { sim.TerrainDetail0, sim.TerrainDetail1, sim.TerrainDetail2, sim.TerrainDetail3 },
                        new float[] { sim.TerrainStartHeight00, sim.TerrainStartHeight01, sim.TerrainStartHeight10, sim.TerrainStartHeight11 },
                        new float[] { sim.TerrainHeightRange00, sim.TerrainHeightRange01, sim.TerrainHeightRange10, sim.TerrainHeightRange11 });

                    fetchingTerrainTexture = false;
                    terrainTextureNeedsUpdate = false;
                });
            }
        }

        private void RenderTerrain(RenderPass pass)
        {
            terrainTimeSinceUpdate += lastFrameTime;

            if (terrainModified && terrainTimeSinceUpdate > RenderSettings.MinimumTimeBetweenTerrainUpdated)
            {
                if (!terrainInProgress)
                {
                    terrainInProgress = true;
                    ResetTerrain(false);
                    UpdateTerrain();
                }
            }

            if (terrainTextureNeedsUpdate)
            {
                UpdateTerrainTexture();
            }

            if (terrainIndices == null || terrainVertices == null) return;

            GL.Color3(1f, 1f, 1f);
            GL.EnableClientState(ArrayCap.VertexArray);
            GL.EnableClientState(ArrayCap.TextureCoordArray);
            GL.EnableClientState(ArrayCap.NormalArray);
            if (pass == RenderPass.Picking)
            {
                GL.EnableClientState(ArrayCap.ColorArray);
                GL.ShadeModel(ShadingModel.Flat);
            }

            if (terrainImage != null)
            {
                if (terrainTexture != -1)
                {
                    GL.DeleteTexture(terrainTexture);
                }

                terrainTexture = GLLoadImage(terrainImage, false);
                terrainImage.Dispose();
                terrainImage = null;
            }

            if (pass != RenderPass.Picking && terrainTexture != -1)
            {
                GL.Enable(EnableCap.Texture2D);
                GL.BindTexture(TextureTarget.Texture2D, terrainTexture);
            }

            if (!RenderSettings.UseVBO)
            {
                unsafe
                {
                    fixed (float* normalPtr = &terrainVertices[0].Vertex.Normal.X)
                    fixed (float* texPtr = &terrainVertices[0].Vertex.TexCoord.X)
                    fixed (byte* colorPtr = &terrainVertices[0].Color.R)
                    {
                        GL.NormalPointer(NormalPointerType.Float, ColorVertex.Size, (IntPtr)normalPtr);
                        GL.TexCoordPointer(2, TexCoordPointerType.Float, ColorVertex.Size, (IntPtr)texPtr);
                        GL.VertexPointer(3, VertexPointerType.Float, ColorVertex.Size, terrainVertices);
                        if (pass == RenderPass.Picking)
                        {
                            GL.ColorPointer(4, ColorPointerType.UnsignedByte, ColorVertex.Size, (IntPtr)colorPtr);
                        }
                        GL.DrawElements(BeginMode.Triangles, terrainIndices.Length, DrawElementsType.UnsignedShort, terrainIndices);
                    }
                }
            }
            else
            {
                if (terrainVBO == -1)
                {
                    GL.GenBuffers(1, out terrainVBO);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, terrainVBO);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(terrainVertices.Length * ColorVertex.Size), terrainVertices, BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, terrainVBO);
                }

                if (terrainIndexVBO == -1)
                {
                    GL.GenBuffers(1, out terrainIndexVBO);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, terrainIndexVBO);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(terrainIndices.Length * sizeof(ushort)), terrainIndices, BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, terrainIndexVBO);
                }

                GL.NormalPointer(NormalPointerType.Float, ColorVertex.Size, (IntPtr)12);
                GL.TexCoordPointer(2, TexCoordPointerType.Float, ColorVertex.Size, (IntPtr)(24));
                if (pass == RenderPass.Picking)
                {
                    GL.ColorPointer(4, ColorPointerType.UnsignedByte, ColorVertex.Size, (IntPtr)32);
                }
                GL.VertexPointer(3, VertexPointerType.Float, ColorVertex.Size, (IntPtr)(0));

                GL.DrawElements(BeginMode.Triangles, terrainIndices.Length, DrawElementsType.UnsignedShort, IntPtr.Zero);

                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            }

            if (pass == RenderPass.Picking)
            {
                GL.DisableClientState(ArrayCap.ColorArray);
                GL.ShadeModel(ShadingModel.Smooth);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, 0);
            }
            GL.DisableClientState(ArrayCap.VertexArray);
            GL.DisableClientState(ArrayCap.TextureCoordArray);
            GL.DisableClientState(ArrayCap.NormalArray);
        }
        #endregion Terrain

        private void ResetMaterial()
        {
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Ambient, new float[] { 0.2f, 0.2f, 0.2f, 1.0f });
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Diffuse, new float[] { 0.8f, 0.8f, 0.8f, 1.0f });
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Specular, new float[] { 0f, 0f, 0f, 1.0f });
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Emission, new float[] { 0f, 0f, 0f, 1.0f });
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Shininess, 0f);
        }

        float LODFactor(float distance, float radius)
        {
            return radius * radius / distance;
        }

        void RenderSphere(float cx, float cy, float cz, float r, int p)
        {
            GL.PushAttrib(AttribMask.AllAttribBits);
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Dither);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.LineStipple);
            GL.Disable(EnableCap.PolygonStipple);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.DepthTest);

            const float TWOPI = 6.28318530717958f;
            const float PIDIV2 = 1.57079632679489f;

            float theta1 = 0.0f;
            float theta2 = 0.0f;
            float theta3 = 0.0f;

            float ex = 0.0f;
            float ey = 0.0f;
            float ez = 0.0f;

            float px = 0.0f;
            float py = 0.0f;
            float pz = 0.0f;

            // Disallow a negative number for radius.
            if (r < 0)
                r = -r;

            // Disallow a negative number for precision.
            if (p < 0)
                p = -p;

            // If the sphere is too small, just render a OpenGL point instead.
            if (p < 4 || r <= 0)
            {
                GL.Begin(BeginMode.Points);
                GL.Vertex3(cx, cy, cz);
                GL.End();
                return;
            }

            for (int i = 0; i < p / 2; ++i)
            {
                theta1 = i * TWOPI / p - PIDIV2;
                theta2 = (i + 1) * TWOPI / p - PIDIV2;

                GL.Begin(BeginMode.TriangleStrip);
                {
                    for (int j = 0; j <= p; ++j)
                    {
                        theta3 = j * TWOPI / p;

                        ex = (float)(Math.Cos(theta2) * Math.Cos(theta3));
                        ey = (float)Math.Sin(theta2);
                        ez = (float)(Math.Cos(theta2) * Math.Sin(theta3));
                        px = cx + r * ex;
                        py = cy + r * ey;
                        pz = cz + r * ez;

                        GL.Normal3(ex, ey, ez);
                        GL.TexCoord2(-(j / (float)p), 2 * (i + 1) / (float)p);
                        GL.Vertex3(px, py, pz);

                        ex = (float)(Math.Cos(theta1) * Math.Cos(theta3));
                        ey = (float)Math.Sin(theta1);
                        ez = (float)(Math.Cos(theta1) * Math.Sin(theta3));
                        px = cx + r * ex;
                        py = cy + r * ey;
                        pz = cz + r * ez;

                        GL.Normal3(ex, ey, ez);
                        GL.TexCoord2(-(j / (float)p), 2 * i / (float)p);
                        GL.Vertex3(px, py, pz);
                    }
                }
                GL.End();
            }
            GL.PopAttrib();
        }

        void RenderPrim(RenderPrimitive mesh, RenderPass pass, int primNr)
        {
            if (!AvatarRenderingEnabled && mesh.Attached) return;

            Primitive prim = mesh.Prim;

            // Individual prim matrix
            GL.PushMatrix();

            // Prim roation and position and scale
            GL.MultMatrix(Math3D.CreateSRTMatrix(prim.Scale, mesh.RenderRotation, mesh.RenderPosition));

            // Do we have animated texture on this face
            bool animatedTexture = false;

            // Initialise flags tracking what type of faces this prim has
            if (pass == RenderPass.Simple)
            {
                mesh.HasSimpleFaces = false;
            }
            else if (pass == RenderPass.Alpha)
            {
                mesh.HasAlphaFaces = false;
            }

            // Draw the prim faces
            for (int j = 0; j < mesh.Faces.Count; j++)
            {
                Primitive.TextureEntryFace teFace = prim.Textures.GetFace((uint)j);
                Face face = mesh.Faces[j];
                FaceData data = (FaceData)face.UserData;

                if (data == null)
                    continue;

                if (teFace == null)
                    continue;

                // Don't render transparent faces
                Color4 RGBA = teFace.RGBA;

                if (data.TextureInfo.FullAlpha || RGBA.A <= 0.01f) continue;

                bool switchedLightsOff = false;

                if (pass != RenderPass.Picking)
                {
                    bool belongToAlphaPass = (RGBA.A < 0.99f) || (data.TextureInfo.HasAlpha && !data.TextureInfo.IsMask);

                    if (belongToAlphaPass && pass != RenderPass.Alpha) continue;
                    if (!belongToAlphaPass && pass == RenderPass.Alpha) continue;

                    if (pass == RenderPass.Simple)
                    {
                        mesh.HasSimpleFaces = true;
                    }
                    else if (pass == RenderPass.Alpha)
                    {
                        mesh.HasAlphaFaces = true;
                    }

                    if (teFace.Fullbright)
                    {
                        GL.Disable(EnableCap.Lighting);
                        switchedLightsOff = true;
                    }

                    switch (teFace.Shiny)
                    {
                        case Shininess.High:
                            GL.Material(MaterialFace.Front, MaterialParameter.Shininess, 0.94f);
                            break;

                        case Shininess.Medium:
                            GL.Material(MaterialFace.Front, MaterialParameter.Shininess, 0.64f);
                            break;

                        case Shininess.Low:
                            GL.Material(MaterialFace.Front, MaterialParameter.Shininess, 0.24f);
                            break;


                        case Shininess.None:
                        default:
                            GL.Material(MaterialFace.Front, MaterialParameter.Shininess, 0f);
                            break;
                    }

                    var faceColor = new float[] { RGBA.R, RGBA.G, RGBA.B, RGBA.A };
                    GL.Color4(faceColor);

                    GL.Material(MaterialFace.Front, MaterialParameter.Specular, new float[] { 0.5f, 0.5f, 0.5f, 1f });

                    if (data.TextureInfo.TexturePointer == 0 && TexturesPtrMap.ContainsKey(teFace.TextureID))
                    {
                        data.TextureInfo = TexturesPtrMap[teFace.TextureID];
                    }

                    if (data.TextureInfo.TexturePointer == 0)
                    {
                        GL.Disable(EnableCap.Texture2D);
                        if (texturesRequestedThisFrame < RenderSettings.TexturesToDownloadPerFrame && !data.TextureInfo.FetchFailed)
                        {
                            texturesRequestedThisFrame++;

                            DownloadTexture(new TextureLoadItem()
                            {
                                Prim = prim,
                                TeFace = teFace,
                                Data = data
                            });
                        }
                    }
                    else
                    {
                        // Is this face using texture animation
                        if ((prim.TextureAnim.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0
                            && (prim.TextureAnim.Face == j || prim.TextureAnim.Face == 255))
                        {
                            if (data.AnimInfo == null)
                            {
                                data.AnimInfo = new TextureAnimationInfo();
                            }
                            data.AnimInfo.PrimAnimInfo = prim.TextureAnim;
                            data.AnimInfo.Step(lastFrameTime);
                            animatedTexture = true;
                        }
                        else if (data.AnimInfo != null) // Face texture not animated. Do we have previous anim setting?
                        {
                            data.AnimInfo = null;
                        }

                        GL.Enable(EnableCap.Texture2D);
                        GL.BindTexture(TextureTarget.Texture2D, data.TextureInfo.TexturePointer);
                    }

                }
                else
                {
                    data.PickingID = primNr;
                    var primNrBytes = Utils.UInt16ToBytes((ushort)primNr);
                    var faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)j, 255 };
                    GL.Color4(faceColor);
                }

                if (!RenderSettings.UseVBO)
                {
                    Vertex[] verts = face.Vertices.ToArray();
                    ushort[] indices = face.Indices.ToArray();

                    unsafe
                    {
                        fixed (float* normalPtr = &verts[0].Normal.X)
                        fixed (float* texPtr = &verts[0].TexCoord.X)
                        {
                            GL.NormalPointer(NormalPointerType.Float, FaceData.VertexSize, (IntPtr)normalPtr);
                            GL.TexCoordPointer(2, TexCoordPointerType.Float, FaceData.VertexSize, (IntPtr)texPtr);
                            GL.VertexPointer(3, VertexPointerType.Float, FaceData.VertexSize, verts);
                            GL.DrawElements(BeginMode.Triangles, indices.Length, DrawElementsType.UnsignedShort, indices);
                        }
                    }
                }
                else
                {
                    data.CheckVBO(face);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, data.VertexVBO);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, data.IndexVBO);
                    GL.NormalPointer(NormalPointerType.Float, FaceData.VertexSize, (IntPtr)12);
                    GL.TexCoordPointer(2, TexCoordPointerType.Float, FaceData.VertexSize, (IntPtr)(24));
                    GL.VertexPointer(3, VertexPointerType.Float, FaceData.VertexSize, (IntPtr)(0));

                    GL.DrawElements(BeginMode.Triangles, face.Indices.Count, DrawElementsType.UnsignedShort, IntPtr.Zero);

                    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

                }

                if (switchedLightsOff)
                {
                    GL.Enable(EnableCap.Lighting);
                    switchedLightsOff = false;
                }
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            ResetMaterial();

            // Reset texture coordinates if we modified them in texture animation
            if (animatedTexture)
            {
                GL.MatrixMode(MatrixMode.Texture);
                GL.LoadIdentity();
                GL.MatrixMode(MatrixMode.Modelview);
            }

            // Pop the prim matrix
            GL.PopMatrix();
        }

        void SortCullInterpolate()
        {
            SortedObjects = new List<SceneObject>();
            VisibleAvatars = new List<RenderAvatar>();
            if (RenderSettings.OcclusionCullingEnabled)
            {
                OccludedObjects = new List<SceneObject>();
            }

            lock (Prims)
            {
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    obj.PositionCalculated = false;
                }

                // Calculate positions and rotations of root prims
                // Perform interpolation om objects that survive culling
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    if (obj.BasePrim.ParentID != 0) continue;
                    if (!obj.Initialized) obj.Initialize();

                    obj.Step(lastFrameTime);

                    PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                    obj.DistanceSquared = FindClosestDistanceSquared(Camera.RenderPosition, obj);
                    obj.PositionCalculated = true;

                    if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;
                    if (LODFactor(obj.DistanceSquared, obj.BoundingVolume.ScaledR) < minLODFactor) continue;

                    if (!obj.Meshed)
                    {
                        if (!obj.Meshing && meshingsRequestedThisFrame < RenderSettings.MeshesPerFrame)
                        {
                            meshingsRequestedThisFrame++;
                            MeshPrim(obj);
                        }
                    }

                    if (obj.Faces == null) continue;

                    obj.Attached = false;
                    if (obj.Occluded())
                    {
                        OccludedObjects.Add(obj);
                    }
                    else
                    {
                        SortedObjects.Add(obj);
                    }
                }

                // Calculate avatar positions and perform interpolation tasks
                lock (Avatars)
                {
                    foreach (RenderAvatar obj in Avatars.Values)
                    {
                        if (!obj.Initialized) obj.Initialize();
                        if (AvatarRenderingEnabled) obj.Step(lastFrameTime);
                        PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                        obj.DistanceSquared = FindClosestDistanceSquared(Camera.RenderPosition, obj);
                        obj.PositionCalculated = true;

                        if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;

                        VisibleAvatars.Add(obj);
                        // SortedObjects.Add(obj);
                    }
                }

                // Calculate position and rotations of child objects
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    if (obj.BasePrim.ParentID == 0) continue;
                    if (!obj.Initialized) obj.Initialize();

                    obj.Step(lastFrameTime);

                    if (!obj.PositionCalculated)
                    {
                        PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                        obj.DistanceSquared = FindClosestDistanceSquared(Camera.RenderPosition, obj);
                        obj.PositionCalculated = true;
                    }

                    if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;
                    if (LODFactor(obj.DistanceSquared, obj.BoundingVolume.ScaledR) < minLODFactor) continue;

                    if (!obj.Meshed)
                    {
                        if (!obj.Meshing && meshingsRequestedThisFrame < RenderSettings.MeshesPerFrame)
                        {
                            meshingsRequestedThisFrame++;
                            MeshPrim(obj);
                        }
                    }

                    if (obj.Faces == null) continue;

                    if (!obj.AttachedStateKnown)
                    {
                        obj.Attached = IsAttached(obj.BasePrim.ParentID);
                        obj.AttachedStateKnown = true;
                    }

                    if (obj.Occluded())
                    {
                        OccludedObjects.Add(obj);
                    }
                    else
                    {
                        SortedObjects.Add(obj);
                    }
                }
            }

            // RenderPrimitive class has IComparable implementation
            // that allows sorting by distance
            SortedObjects.Sort();
        }

        void RenderBoundingBox(SceneObject prim)
        {
            Vector3 scale = prim.BasePrim.Scale;

            GL.PushMatrix();
            GL.MultMatrix(Math3D.CreateSRTMatrix(scale, prim.RenderRotation, prim.RenderPosition));

            if (RenderSettings.UseVBO)
            {
                GL.DrawElements(BeginMode.Quads, RHelp.CubeIndices.Length, DrawElementsType.UnsignedShort, IntPtr.Zero);
            }
            else
            {
                GL.VertexPointer(3, VertexPointerType.Float, 0, RHelp.CubeVertices);
                GL.DrawElements(BeginMode.Quads, RHelp.CubeIndices.Length, DrawElementsType.UnsignedShort, RHelp.CubeIndices);
            }
            GL.PopMatrix();
        }

        int boundingBoxVBO = -1;
        int boundingBoxVIndexVBO = -1;

        private void RenderOccludedObjects()
        {
            if (!RenderSettings.OcclusionCullingEnabled) return;

            GL.EnableClientState(ArrayCap.VertexArray);
            GL.ColorMask(false, false, false, false);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Lighting);

            if (RenderSettings.UseVBO)
            {
                if (boundingBoxVBO == -1)
                {
                    GL.GenBuffers(1, out boundingBoxVBO);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, boundingBoxVBO);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(sizeof(float) * RHelp.CubeVertices.Length), RHelp.CubeVertices, BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, boundingBoxVBO);
                }

                if (boundingBoxVIndexVBO == -1)
                {
                    GL.GenBuffers(1, out boundingBoxVIndexVBO);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, boundingBoxVIndexVBO);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(sizeof(ushort) * RHelp.CubeIndices.Length), RHelp.CubeIndices, BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, boundingBoxVIndexVBO);
                }

                GL.VertexPointer(3, VertexPointerType.Float, 0, (IntPtr)0);
            }

            foreach (SceneObject obj in OccludedObjects)
            {
                if ((!obj.HasAlphaFaces && !obj.HasSimpleFaces)) continue;
                obj.HasSimpleFaces = true;
                obj.HasAlphaFaces = false;
                obj.StartSimpleQuery();
                RenderBoundingBox(obj);
                obj.EndSimpleQuery();
            }

            if (RenderSettings.UseVBO)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            }

            GL.ColorMask(true, true, true, true);
            GL.DisableClientState(ArrayCap.VertexArray);
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.Lighting);
        }

        private void RenderObjects(RenderPass pass)
        {
            if (!PrimitiveRenderingEnabled) return;

            GL.EnableClientState(ArrayCap.VertexArray);
            GL.EnableClientState(ArrayCap.TextureCoordArray);
            GL.EnableClientState(ArrayCap.NormalArray);

            Vector3 myPos = Vector3.Zero;
            RenderAvatar me;
            if (Avatars.TryGetValue(Client.Self.LocalID, out me))
            {
                myPos = me.RenderPosition;
            }
            else
            {
                myPos = Client.Self.SimPosition;
            }

            int nrPrims = SortedObjects.Count;
            for (int i = 0; i < nrPrims; i++)
            {
                //RenderBoundingBox(SortedPrims[i]);

                // When rendering alpha faces, draw from back towards the camers
                // otherwise from those closest to camera, to the farthest
                int ix = pass == RenderPass.Alpha ? nrPrims - i - 1 : i;
                SceneObject obj = SortedObjects[ix];

                if (obj is RenderPrimitive)
                {
                    // Don't render objects that are outside the draw distane
                    if (Vector3.DistanceSquared(myPos, obj.RenderPosition) > drawDistanceSquared) continue;
                    obj.StartQuery(pass);
                    RenderPrim((RenderPrimitive)obj, pass, ix);
                    obj.EndQuery(pass);
                }
            }

            GL.Disable(EnableCap.Texture2D);
            GL.DisableClientState(ArrayCap.VertexArray);
            GL.DisableClientState(ArrayCap.TextureCoordArray);
            GL.DisableClientState(ArrayCap.NormalArray);
        }

        int texturesRequestedThisFrame;
        int meshingsRequestedThisFrame;
        int meshingsRequestedLastFrame;

        private void Render(bool picking)
        {
            SortCullInterpolate();

            if (picking)
            {
                GL.ClearColor(1f, 1f, 1f, 1f);
            }
            else
            {
                if (RenderSettings.AdvancedWater)
                {
                    GL.ClearColor(0.09f, 0.28f, 0.63f, 1f);
                    CreateReflectionTexture(Client.Network.CurrentSim.WaterHeight, 512);
                    CreateRefractionDepthTexture(Client.Network.CurrentSim.WaterHeight, 512);
                    glControl_Resize(null, null);
                }
                GL.ClearColor(0.39f, 0.58f, 0.93f, 1.0f);
            }

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.LoadIdentity();

            // Setup wireframe or solid fill drawing mode
            if (Wireframe && !picking)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            Camera.LookAt();

            GL.Light(LightName.Light0, LightParameter.Position, sunPos);

            // Push the world matrix
            GL.PushMatrix();

            if (!cameraLocked && trackedObject != null)
            {
                if (lastTrackedObjectPos == RHelp.InvalidPosition)
                {
                    lastTrackedObjectPos = trackedObject.RenderPosition;
                }
                else if (lastTrackedObjectPos != trackedObject.RenderPosition)
                {
                    Vector3 diffPos = (trackedObject.RenderPosition - lastTrackedObjectPos);
                    Camera.Position += diffPos;
                    Camera.FocalPoint += diffPos;
                    lastTrackedObjectPos = trackedObject.RenderPosition;
                }
            }

            if (Camera.Modified)
            {
                GL.GetFloat(GetPName.ProjectionMatrix, out ProjectionMatrix);
                GL.GetFloat(GetPName.ModelviewMatrix, out ModelMatrix);
                GL.GetInteger(GetPName.Viewport, Viewport);
                Frustum.CalculateFrustum(ProjectionMatrix, ModelMatrix);
                UpdateCamera();
                Camera.Modified = false;
                Camera.Step(lastFrameTime);
            }

            if (picking)
            {
                GL.Disable(EnableCap.Lighting);
                RenderTerrain(RenderPass.Picking);
                RenderObjects(RenderPass.Picking);
                RenderAvatars(RenderPass.Picking);
                GLHUDBegin();
                RenderText(RenderPass.Picking);
                GLHUDEnd();
                GL.Enable(EnableCap.Lighting);
            }
            else
            {
                texturesRequestedThisFrame = 0;
                meshingsRequestedLastFrame = meshingsRequestedThisFrame;
                meshingsRequestedThisFrame = 0;

                CheckKeyboard(lastFrameTime);

                RenderTerrain(RenderPass.Simple);

                // Alpha mask elements, no blending, alpha test for A > 0.5
                GL.Enable(EnableCap.AlphaTest);
                RenderObjects(RenderPass.Simple);
                RenderAvatarsSkeleton(RenderPass.Simple);
                RenderAvatars(RenderPass.Simple);
                GL.Disable(EnableCap.AlphaTest);

                GL.DepthMask(false);
                RenderOccludedObjects();

                // Alpha blending elements, disable writing to depth buffer
                GL.Enable(EnableCap.Blend);
                RenderWater();
                RenderObjects(RenderPass.Alpha);
                GL.DepthMask(true);

                GLHUDBegin();
                RenderText(RenderPass.Simple);
                RenderStats();
                GLHUDEnd();
                GL.Disable(EnableCap.Blend);
            }

            // Pop the world matrix
            GL.PopMatrix();
        }

        private void GluPerspective(float fovy, float aspect, float zNear, float zFar)
        {
            float fH = (float)Math.Tan(fovy / 360 * (float)Math.PI) * zNear;
            float fW = fH * aspect;
            GL.Frustum(-fW, fW, -fH, fH, zNear, zFar);
        }

        private bool TryPick(int x, int y, out object picked, out int faceID)
        {
            // Save old attributes
            GL.PushAttrib(AttribMask.AllAttribBits);

            // Disable some attributes to make the objects flat / solid color when they are drawn
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Dither);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.LineStipple);
            GL.Disable(EnableCap.PolygonStipple);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.AlphaTest);

            Render(true);

            byte[] color = new byte[4];
            GL.ReadPixels(x, glControl.Height - y, 1, 1, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, color);

            GL.PopAttrib();

            int primID = Utils.BytesToUInt16(color, 0);
            faceID = color[2];

            picked = null;

            if (color[3] == 253) // Terrain
            {
                int vertexIndex = Utils.BytesToInt(new byte[] { color[0], color[1], color[2], 0 });
                if (vertexIndex < terrainVertices.Length)
                {
                    picked = terrainVertices[vertexIndex].Vertex.Position;
                    return true;
                }
            }
            else if (color[3] == 254) // Avatar
            {
                lock (VisibleAvatars)
                {
                    foreach (var avatar in VisibleAvatars)
                    {
                        for (int i = 0; i < avatar.data.Length; i++)
                        {
                            var face = avatar.data[i];
                            if (face != null && face.PickingID == primID)
                            {
                                picked = avatar;
                                break;
                            }
                        }
                    }
                }

                if (picked != null)
                {
                    return true;
                }
            }
            else if (color[3] == 255) // Prim
            {
                lock (SortedObjects)
                {
                    foreach (SceneObject obj in SortedObjects)
                    {
                        if (!(obj is RenderPrimitive)) continue;
                        RenderPrimitive prim = (RenderPrimitive)obj;

                        if (obj.BasePrim.LocalID == 0)
                            continue;

                        foreach (var face in prim.Faces)
                        {
                            if (face.UserData == null) continue;
                            if (((FaceData)face.UserData).PickingID == primID)
                            {
                                picked = prim;
                                break;
                            }
                        }

                        if (picked != null) break;
                    }
                }
            }

            return picked != null;
        }

        public void DownloadTexture(TextureLoadItem item)
        {
            lock (TexturesPtrMap)
            {
                if (TexturesPtrMap.ContainsKey(item.TeFace.TextureID))
                {
                    item.Data.TextureInfo = TexturesPtrMap[item.TeFace.TextureID];
                }
                else
                {
                    TexturesPtrMap[item.TeFace.TextureID] = item.Data.TextureInfo;

                    if (item.TextureData == null && item.TGAData == null)
                    {
                        if (CacheDecodedTextures && RHelp.LoadCachedImage(item.TeFace.TextureID, out item.TGAData, out item.Data.TextureInfo.HasAlpha, out item.Data.TextureInfo.FullAlpha, out item.Data.TextureInfo.IsMask))
                        {
                            PendingTextures.Enqueue(item);
                        }
                        else if (Client.Assets.Cache.HasAsset(item.Data.TextureInfo.TextureID))
                        {
                            item.LoadAssetFromCache = true;
                            PendingTextures.Enqueue(item);
                        }
                        else if (!item.Data.TextureInfo.FetchFailed)
                        {
                            Client.Assets.RequestImage(item.TeFace.TextureID, (state, asset) =>
                            {
                                switch (state)
                                {
                                    case TextureRequestState.Finished:
                                        item.TextureData = asset.AssetData;
                                        PendingTextures.Enqueue(item);
                                        break;

                                    case TextureRequestState.Aborted:
                                    case TextureRequestState.NotFound:
                                    case TextureRequestState.Timeout:
                                        item.Data.TextureInfo.FetchFailed = true;
                                        break;
                                }
                            });
                        }
                    }
                    else
                    {
                        PendingTextures.Enqueue(item);
                    }
                }
            }
        }

        private void CalculateBoundingBox(RenderPrimitive rprim)
        {
            Primitive prim = rprim.BasePrim;

            // Calculate bounding volumes for each prim and adjust textures
            rprim.BoundingVolume = new BoundingVolume();
            for (int j = 0; j < rprim.Faces.Count; j++)
            {
                Primitive.TextureEntryFace teFace = prim.Textures.GetFace((uint)j);
                if (teFace == null) continue;

                Face face = rprim.Faces[j];
                FaceData data = new FaceData();

                data.BoundingVolume.CreateBoundingVolume(face, prim.Scale);
                rprim.BoundingVolume.AddVolume(data.BoundingVolume, prim.Scale);

                // With linear texture animation in effect, texture repeats and offset are ignored
                if ((prim.TextureAnim.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0
                    && (prim.TextureAnim.Flags & Primitive.TextureAnimMode.ROTATE) == 0
                    && (prim.TextureAnim.Face == 255 || prim.TextureAnim.Face == j))
                {
                    teFace.RepeatU = 1;
                    teFace.RepeatV = 1;
                    teFace.OffsetU = 0;
                    teFace.OffsetV = 0;
                }

                // Need to adjust UV for spheres as they are sort of half-prim
                if (prim.PrimData.ProfileCurve == ProfileCurve.HalfCircle)
                {
                    teFace = (Primitive.TextureEntryFace)teFace.Clone();
                    teFace.RepeatV *= 2;
                    teFace.OffsetV += 0.5f;
                }

                // Sculpt UV vertically flipped compared to prims. Flip back
                if (prim.Sculpt != null && prim.Sculpt.SculptTexture != UUID.Zero && prim.Sculpt.Type != SculptType.Mesh)
                {
                    teFace = (Primitive.TextureEntryFace)teFace.Clone();
                    teFace.RepeatV *= -1;
                }

                // Texture transform for this face
                renderer.TransformTexCoords(face.Vertices, face.Center, teFace, prim.Scale);

                // Set the UserData for this face to our FaceData struct
                face.UserData = data;
                rprim.Faces[j] = face;
            }
        }

        private void MeshPrim(RenderPrimitive rprim)
        {
            if (rprim.Meshing) return;

            rprim.Meshing = true;
            Primitive prim = rprim.BasePrim;

            // Regular prim
            if (prim.Sculpt == null || prim.Sculpt.SculptTexture == UUID.Zero)
            {
                DetailLevel detailLevel = RenderSettings.PrimRenderDetail;
                if (RenderSettings.AllowQuickAndDirtyMeshing)
                {
                    if (prim.Flexible == null && prim.Type == PrimType.Box &&
                        prim.PrimData.ProfileHollow == 0 &&
                        prim.PrimData.PathTwist == 0 &&
                        prim.PrimData.PathTaperX == 0 &&
                        prim.PrimData.PathTaperY == 0 &&
                        prim.PrimData.PathSkew == 0 &&
                        prim.PrimData.PathShearX == 0 &&
                        prim.PrimData.PathShearY == 0 &&
                        prim.PrimData.PathRevolutions == 1 &&
                        prim.PrimData.PathRadiusOffset == 0)
                        detailLevel = DetailLevel.Low;//Its a box or something else that can use lower meshing
                }
                FacetedMesh mesh = renderer.GenerateFacetedMesh(prim, detailLevel);
                rprim.Faces = mesh.Faces;
                CalculateBoundingBox(rprim);
                rprim.Meshing = false;
                rprim.Meshed = true;
            }
            else
            {
                PendingTasks.Enqueue(GenerateSculptOrMeshPrim(rprim, prim));
                return;
            }
        }

        private GenericTask GenerateSculptOrMeshPrim(RenderPrimitive rprim, Primitive prim)
        {
            return new GenericTask(() =>
            {
                FacetedMesh mesh = null;

                try
                {
                    if (prim.Sculpt.Type != SculptType.Mesh)
                    { // Regular sculptie
                        Image img = null;

                        lock (sculptCache)
                        {
                            if (sculptCache.ContainsKey(prim.Sculpt.SculptTexture))
                            {
                                img = sculptCache[prim.Sculpt.SculptTexture];
                            }
                        }

                        if (img == null)
                        {
                            if (LoadTexture(prim.Sculpt.SculptTexture, ref img, true))
                            {
                                sculptCache[prim.Sculpt.SculptTexture] = (Bitmap)img;
                            }
                            else
                            {
                                return;
                            }
                        }

                        mesh = renderer.GenerateFacetedSculptMesh(prim, (Bitmap)img, RenderSettings.SculptRenderDetail);
                    }
                    else
                    { // Mesh
                        AutoResetEvent gotMesh = new AutoResetEvent(false);

                        Client.Assets.RequestMesh(prim.Sculpt.SculptTexture, (success, meshAsset) =>
                        {
                            if (!success || !FacetedMesh.TryDecodeFromAsset(prim, meshAsset, RenderSettings.MeshRenderDetail, out mesh))
                            {
                                Logger.Log("Failed to fetch or decode the mesh asset", Helpers.LogLevel.Warning, Client);
                            }
                            gotMesh.Set();
                        });

                        gotMesh.WaitOne(20 * 1000, false);
                    }
                }
                catch
                { }

                if (mesh != null)
                {
                    rprim.Faces = mesh.Faces;
                    CalculateBoundingBox(rprim);
                    rprim.Meshing = false;
                    rprim.Meshed = true;
                }
                else
                {
                    lock (Prims)
                    {
                        Prims.Remove(rprim.BasePrim.LocalID);
                    }
                }
            });
        }

        private void UpdatePrimBlocking(Primitive prim)
        {
            if (!RenderingEnabled) return;

            if (AvatarRenderingEnabled && prim.PrimData.PCode == PCode.Avatar)
            {
                AddAvatarToScene(Client.Network.CurrentSim.ObjectsAvatars[prim.LocalID]);
                return;
            }

            // Skip foliage
            if (prim.PrimData.PCode != PCode.Prim) return;
            if (!PrimitiveRenderingEnabled) return;

            if (prim.Textures == null) return;

            RenderPrimitive rPrim = null;
            if (Prims.TryGetValue(prim.LocalID, out rPrim))
            {
                rPrim.AttachedStateKnown = false;
            }
            else
            {
                rPrim = new RenderPrimitive();
                rPrim.Meshed = false;
                rPrim.BoundingVolume = new BoundingVolume();
                rPrim.BoundingVolume.FromScale(prim.Scale);
            }

            rPrim.BasePrim = prim;
            lock (Prims) Prims[prim.LocalID] = rPrim;
        }

        private bool LoadTexture(UUID textureID, ref Image texture, bool removeAlpha)
        {
            ManualResetEvent gotImage = new ManualResetEvent(false);
            Image img = null;

            try
            {
                gotImage.Reset();
                bool hasAlpha, fullAlpha, isMask;
                byte[] tgaData;
                if (RHelp.LoadCachedImage(textureID, out tgaData, out hasAlpha, out fullAlpha, out isMask))
                {
                    img = LoadTGAClass.LoadTGA(new MemoryStream(tgaData));
                }
                else
                {
                    instance.Client.Assets.RequestImage(textureID, (TextureRequestState state, AssetTexture assetTexture) =>
                        {
                            if (state == TextureRequestState.Finished)
                            {
                                ManagedImage mi;
                                OpenJPEG.DecodeToImage(assetTexture.AssetData, out mi);

                                if (removeAlpha)
                                {
                                    if ((mi.Channels & ManagedImage.ImageChannels.Alpha) != 0)
                                    {
                                        mi.ConvertChannels(mi.Channels & ~ManagedImage.ImageChannels.Alpha);
                                    }
                                }
                                tgaData = mi.ExportTGA();
                                img = LoadTGAClass.LoadTGA(new MemoryStream(tgaData));
                                RHelp.SaveCachedImage(tgaData, textureID, (mi.Channels & ManagedImage.ImageChannels.Alpha) != 0, false, false);
                            }
                            gotImage.Set();
                        }
                    );
                    gotImage.WaitOne(30 * 1000, false);
                }
                if (img != null)
                {
                    texture = img;
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Logger.Log(e.Message, Helpers.LogLevel.Error, instance.Client, e);
                return false;
            }
        }
        #endregion Private methods (the meat)

        #region Form controls handlers
        private void chkWireFrame_CheckedChanged(object sender, EventArgs e)
        {
            Wireframe = chkWireFrame.Checked;
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            InitCamera();
        }

        private void cbAA_CheckedChanged(object sender, EventArgs e)
        {
            instance.GlobalSettings["use_multi_sampling"] = UseMultiSampling = cbAA.Checked;
            SetupGLControl();
        }

        #endregion Form controls handlers

        #region Context menu
        /// <summary>
        /// Dynamically construct the context menu when we right click on the screen
        /// </summary>
        /// <param name="csender"></param>
        /// <param name="ce"></param>
        private void ctxObjects_Opening(object csender, System.ComponentModel.CancelEventArgs ce)
        {
            // Clear all context menu items
            ctxMenu.Items.Clear();
            ToolStripMenuItem item;

            // Was it prim that was right clicked
            if (RightclickedObject != null && RightclickedObject is RenderPrimitive)
            {
                RenderPrimitive prim = (RenderPrimitive)RightclickedObject;

                // Sit/stand up button handling
                item = new ToolStripMenuItem("Sit", null, (sender, e) =>
                {
                    if (!instance.State.IsSitting)
                    {
                        instance.State.SetSitting(true, prim.Prim.ID);
                    }
                    else
                    {
                        instance.State.SetSitting(false, UUID.Zero);
                    }
                });

                if (instance.State.IsSitting)
                {
                    item.Text = "Stand up";
                }
                else if (prim.Prim.Properties != null
                    && !string.IsNullOrEmpty(prim.Prim.Properties.SitName))
                {
                    item.Text = prim.Prim.Properties.SitName;
                }
                ctxMenu.Items.Add(item);

                // Is the prim touchable
                if ((prim.Prim.Flags & PrimFlags.Touch) != 0)
                {
                    item = new ToolStripMenuItem("Touch", null, (sender, e) =>
                    {
                        Client.Self.Grab(prim.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, RightclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                        Thread.Sleep(100);
                        Client.Self.DeGrab(prim.Prim.LocalID);
                    });

                    if (prim.Prim.Properties != null
                        && !string.IsNullOrEmpty(prim.Prim.Properties.TouchName))
                    {
                        item.Text = prim.Prim.Properties.TouchName;
                    }
                    ctxMenu.Items.Add(item);
                }

                // Can I modify this object?
                if ((prim.Prim.Flags & PrimFlags.ObjectModify) != 0)
                {
                    // Take button
                    item = new ToolStripMenuItem("Take", null, (sender, e) =>
                    {
                        instance.MediaManager.PlayUISound(UISounds.ObjectDelete);
                        Client.Inventory.RequestDeRezToInventory(prim.Prim.LocalID);
                    });
                    ctxMenu.Items.Add(item);

                    // Delete button
                    item = new ToolStripMenuItem("Delete", null, (sender, e) =>
                    {
                        instance.MediaManager.PlayUISound(UISounds.ObjectDelete);
                        Client.Inventory.RequestDeRezToInventory(prim.Prim.LocalID, DeRezDestination.AgentInventoryTake, Client.Inventory.FindFolderForType(AssetType.TrashFolder), UUID.Random());
                    });
                    ctxMenu.Items.Add(item);


                }
            }

            // If we are not the sole menu item, add separator
            if (ctxMenu.Items.Count > 0)
            {
                ctxMenu.Items.Add(new ToolStripSeparator());
            }


            // Dock/undock menu item
            bool docked = !instance.TabConsole.Tabs["scene_window"].Detached;
            if (docked)
            {
                item = new ToolStripMenuItem("Undock", null, (sender, e) =>
                {
                    instance.TabConsole.SelectDefaultTab();
                    instance.TabConsole.Tabs["scene_window"].Detach(instance);
                });
            }
            else
            {
                item = new ToolStripMenuItem("Dock", null, (sender, e) =>
                {
                    Control p = Parent;
                    instance.TabConsole.Tabs["scene_window"].AttachTo(instance.TabConsole.tstTabs, instance.TabConsole.toolStripContainer1.ContentPanel);
                    if (p is Form)
                    {
                        ((Form)p).Close();
                    }
                });
            }
            ctxMenu.Items.Add(item);
        }
        #endregion Context menu

        #region Winform hooks

        private void hsAmbient_Scroll(object sender, ScrollEventArgs e)
        {
            ambient = (float)hsAmbient.Value / 100f;
            SetSun();
        }

        private void hsDiffuse_Scroll(object sender, ScrollEventArgs e)
        {
            difuse = (float)hsDiffuse.Value / 100f;
            SetSun();
        }

        private void hsSpecular_Scroll(object sender, ScrollEventArgs e)
        {
            specular = (float)hsSpecular.Value / 100f;
            SetSun();
        }

        private void hsLOD_Scroll(object sender, ScrollEventArgs e)
        {
            minLODFactor = (float)hsLOD.Value / 5000f;
        }

        private void button_vparam_Click(object sender, EventArgs e)
        {
            //int paramid = int.Parse(textBox_vparamid.Text);
            //float weight = (float)hScrollBar_weight.Value/100f;
            float weightx = float.Parse(textBox_x.Text);
            float weighty = float.Parse(textBox_y.Text);
            float weightz = float.Parse(textBox_z.Text);

            foreach (RenderAvatar av in Avatars.Values)
            {
                //av.glavatar.morphtest(av.avatar,paramid,weight);
                av.glavatar.skel.deformbone(comboBox1.Text, new Vector3(0, 0, 0), new Vector3(float.Parse(textBox_sx.Text), float.Parse(textBox_sy.Text), float.Parse(textBox_sz.Text)), Quaternion.CreateFromEulers((float)(Math.PI * (weightx / 180)), (float)(Math.PI * (weighty / 180)), (float)(Math.PI * (weightz / 180))));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }
        }

        private void textBox_vparamid_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

            string bone = comboBox1.Text;
            foreach (RenderAvatar av in Avatars.Values)
            {
                Bone b;
                if (av.glavatar.skel.mBones.TryGetValue(bone, out b))
                {
                    textBox_sx.Text = (b.scale.X - 1.0f).ToString();
                    textBox_sy.Text = (b.scale.Y - 1.0f).ToString();
                    textBox_sz.Text = (b.scale.Z - 1.0f).ToString();

                    float x, y, z;
                    b.rot.GetEulerAngles(out x, out y, out z);
                    textBox_x.Text = x.ToString();
                    textBox_y.Text = y.ToString();
                    textBox_z.Text = z.ToString();

                }

            }


        }

        private void textBox_y_TextChanged(object sender, EventArgs e)
        {

        }

        private void textBox_z_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox_morph_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void button1_Click(object sender, EventArgs e)
        {
            foreach (RenderAvatar av in Avatars.Values)
            {
                int id = -1;
                foreach (VisualParamEx vpe in VisualParamEx.morphParams.Values)
                {
                    if (vpe.Name == comboBox_morph.Text)
                    {
                        id = vpe.ParamID;
                        break;
                    }

                }
                av.glavatar.morphtest(av.avatar, id, float.Parse(textBox_morphamount.Text));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }



        }

        private void gbZoom_Enter(object sender, EventArgs e)
        {

        }

        private void button_driver_Click(object sender, EventArgs e)
        {
            foreach (RenderAvatar av in Avatars.Values)
            {
                int id = -1;
                foreach (VisualParamEx vpe in VisualParamEx.drivenParams.Values)
                {
                    if (vpe.Name == comboBox_driver.Text)
                    {
                        id = vpe.ParamID;
                        break;
                    }

                }
                av.glavatar.morphtest(av.avatar, id, float.Parse(textBox_driveramount.Text));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }

        }

        private void tbDrawDistance_Scroll(object sender, EventArgs e)
        {
            DrawDistance = (float)tbDrawDistance.Value;
            lblDrawDistance.Text = string.Format("Draw distance: {0}", tbDrawDistance.Value);
            UpdateCamera();
        }

        bool miscEnabled = true;
        private void cbMisc_CheckedChanged(object sender, EventArgs e)
        {
            miscEnabled = cbMisc.Checked;
            RenderSettings.OcclusionCullingEnabled = miscEnabled;
        }

        #endregion
    }
}
