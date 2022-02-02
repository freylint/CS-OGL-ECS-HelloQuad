//! A Hello-Triangle app to toy with the dotnet game-dev ecosystem

using System.Diagnostics;
using DefaultEcs;
using FluentResults;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Termina;

internal static class Program
{
    private static void Main()
    {
        // Get config
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(800, 600);
        options.Title = "Termina";

        // Allocate world resources
        var world = new World();
        var window = Window.Create(options);

        //Assign window callbacks
        window.Load += () => OnLoad(ref window, ref world);
        window.Update += _ => { };
        window.Render += _ =>
        {
            // Validate program state
            Debug.Assert(world.Has<GL>());

            // Get ECS data
            var gl = world.Get<GL>();
            var indices = Array.Empty<uint>();
            foreach (var (str, bdata) in world.GetAll<(string, uint[])>())
            {
                if (str == "Indices")
                {
                    indices = bdata;
                }
                else
                {
                    throw new NotImplementedException();
                }
            }

            // Render state
            gl.Clear((uint) ClearBufferMask.ColorBufferBit);

            //Bind the geometry and shader.

            //Draw the geometry.
            unsafe
            {
                gl.DrawElements(PrimitiveType.Triangles, (uint) indices.Length, DrawElementsType.UnsignedInt, null);
            }
        };
        window.Closing += () =>
        {
            var gl = world.Get<GL>();

            // TODO add wrapper type to buffers to non OpenGL buffer uints in world
            gl.DeleteBuffers(world.GetAll<uint>());
            gl.DeleteVertexArrays(world.GetAll<VertexArray>());
        };

        //Run the window.
        window.Run();

        // Deallocate bound memory
    }

    private static void OnLoad(ref IWindow window, ref World world)
    {
        // Bootstrap program
        var glRes = BootstrapGl(ref window);
        var inputRes = BootstrapInput(window);

        // Propagate errors
        var results = new[] {glRes, inputRes};

        foreach (var res in results)
        {
            if (res.IsFailed) throw new Exception(res.ToString());
        }

        // Unwrap results
        var gl = glRes.Value;

        // Bootstrap ECS
        var ecsRes = BootstrapEcs(ref world, ref gl);

        // Check that ECS is in valid state
        if (ecsRes.IsFailed) throw new Exception(ecsRes.ToString());

        // Initialize OpenGl
        InitGl(ref gl, ref world);
    }

    private static Result BootstrapEcs(ref World world, ref GL gl)
    {
        // Put OpenGl Api interface into world
        world.Set(gl);
        return Result.Ok();
    }

    private static Result<GL> BootstrapGl(ref IWindow window)
    {
        var gl = GL.GetApi(window);
        return Result.Ok(gl);
    }

    private static Result BootstrapInput(IView window)
    {
        //Set-up input context.
        var input = window.CreateInput();
        foreach (var board in input.Keyboards)
        {
            board.KeyDown += (_, arg2, _) =>
            {
                //Check to close the window on escape.
                if (arg2 == Key.Escape)
                {
                    window.Close();
                }
            };
        }

        return Result.Ok();
    }

    private static void InitGl(ref GL gl, ref World world)
    {
        // Create OpenGL buffers
        var vao = gl.GenVertexArray();
        var vbo = gl.GenBuffer();
        var ebo = gl.GenBuffer();

        // Create OpenGL shader objects
        var shader = gl.CreateProgram();
        var vertexShader = gl.CreateShader(ShaderType.VertexShader);
        var fragmentShader = gl.CreateShader(ShaderType.FragmentShader);

        // Bind OpenGL buffers
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);

        // Load assets
        var vertsRes = ResourceLoader.LoadParsedAsset<float>("assets/triangle.mdl.csv");
        var indRes = ResourceLoader.LoadParsedAsset<uint>("assets/triangle.ind.csv");

        var fShaderRes = ResourceLoader.LoadShader("res/hello.frag");
        var vShaderRes = ResourceLoader.LoadShader("res/hello.vert");

        // TODO Propagate asset loading errors

        // Unwrap asset data
        var vertices = vertsRes.Value;
        var indices = indRes.Value;
        var fShader = fShaderRes.Value;
        var vShader = vShaderRes.Value;

        // Put necessary data into world
        world.Set<(string, uint[])>(("Indices", indices));


        // Buffer asset data
        unsafe
        {
            // Buffer vertex data
            fixed (void* v = &vertices[0])
            {
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (vertices.Length * sizeof(uint)), v,
                    BufferUsageARB.StaticDraw);
            }

            // Buffer index data
            fixed (void* i = &indices[0])
            {
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint) (indices.Length * sizeof(uint)), i,
                    BufferUsageARB.StaticDraw);
            }
        }

        // Compile shaders
        gl.ShaderSource(vertexShader, vShader);
        gl.ShaderSource(fragmentShader, fShader);

        gl.CompileShader(vertexShader);
        gl.CompileShader(fragmentShader);

        // Check the shader for compilation errors.
        var fShaderLog = gl.GetShaderInfoLog(fragmentShader);
        if (!string.IsNullOrWhiteSpace(fShaderLog))
        {
            Console.WriteLine($"Error compiling fragment shader {fShaderLog}");
        }

        var vShaderLog = gl.GetShaderInfoLog(vertexShader);
        if (!string.IsNullOrWhiteSpace(vShaderLog))
        {
            Console.WriteLine($"Error compiling fragment shader {vShaderLog}");
        }

        // Link shaders.
        gl.AttachShader(shader, vertexShader);
        gl.AttachShader(shader, fragmentShader);
        gl.LinkProgram(shader);

        gl.GetProgram(shader, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            Console.WriteLine($"Error linking shader {gl.GetProgramInfoLog(shader)}");
        }

        // Detach unnecessary shaders
        gl.DetachShader(shader, vertexShader);
        gl.DetachShader(shader, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        // Configure OpenGL pipeline.
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);
        }

        gl.EnableVertexAttribArray(0);

        gl.UseProgram(shader);
            
        // Insert resources into world
        world.Set(("Indices", indices));
    }
}

internal static class ResourceLoader
{
    public static Result<T[]> LoadParsedAsset<T>(string relPath)
    {
        // Read file into string
        var raw = File.ReadAllText(relPath);

        // Split csv data on comma
        var data = raw.Split(',');
        var parsed = new T[data.Length];

        // Parse data
        for (var i = 0; i < data.Length; i++)
        {
            // Sanitize data
            var sanitized = data[i].Trim();
            sanitized = sanitized.Replace("f", "");

            // NOTE This can fail with garbage data
            //  Due to the simplicity of this project I'm 
            //  ignoring that problem.
            //
            // NOTE This can fail if the type does not convert into T
            //
            // Accumulate parsed data into parsed[]
            parsed[i] = (T) Convert.ChangeType(sanitized, typeof(T));
        }

        return Result.Ok(parsed);
    }

    public static Result<string> LoadShader(string path)
    {
        return Result.Ok(File.ReadAllText(path));
    }
}