using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.CompilerServices;

namespace WarpCore
{
    public class SileroVAD : IVoiceActivityDetector, IDisposable
    {
        private readonly InferenceSession _inferenceSession;

        /// <summary>
        /// Number of samples in a window, for 16kHz sample rate
        /// </summary>
        public static readonly int WindowSize = 512;

        /// <summary>
        /// Number of samples added for context, for 16kHz sample rate
        /// </summary>
        public static readonly int ContextSize = 64;


        /// <summary>
        /// <see href="https://github.com/snakers4/silero-vad/issues/426#issuecomment-1977932369">
        /// "Batching is complicated and error-prone, and we dicourage users against using it." - snakers4
        /// </see>
        /// </summary>
        public static readonly int BatchSize = 1;


        /// <summary>
        /// Sample rate passed to the model
        /// </summary>
        private static readonly long[] SampleRate = [16000L];



        public SileroVAD(string modelPath)
        {
            _inferenceSession = new InferenceSession(modelPath,
                new SessionOptions
                {
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = true,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = 0, // Default value: sets to number of physical cores
                });
        }
        public async Task<VADResult> DetectVoiceActivity(FileInfo audioFile)
        {

            await using var fs = new FileStream(
                audioFile.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            byte[] buffer = new byte[(ContextSize + WindowSize) * sizeof(float)];
            Memory<byte> bufferMemory = buffer;
            Memory<float> floatMemory = Unsafe.As<Memory<byte>, Memory<float>>(ref bufferMemory)[..(ContextSize + WindowSize)];

            long windowCount = (audioFile.Length / sizeof(float)) / WindowSize;
            int lastSamples = (int)((audioFile.Length / sizeof(float)) % WindowSize);
            float[] probabilities = new float[windowCount+((lastSamples>0)?1:0)];
            

            NamedOnnxValue input = NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(floatMemory, [BatchSize, WindowSize + ContextSize]));
            NamedOnnxValue sr = NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(SampleRate, [1], false));
            Tensor<float> stateTensor = new DenseTensor<float>([2, 1, 128]);
            NamedOnnxValue state = NamedOnnxValue.CreateFromTensor("state", stateTensor);

            for (long a = 0; a < windowCount; a++)
            {
  
                await fs.ReadExactlyAsync(bufferMemory.Slice(ContextSize * sizeof(float), WindowSize * sizeof(float)));

                var inputs = new List<NamedOnnxValue>
                {
                    input,
                    state,
                    sr,
                };
                using var outputs = _inferenceSession.Run(inputs);
                probabilities[a] = outputs[0].AsTensor<float>()[0];
                stateTensor = outputs[1].AsTensor<float>().Clone();
                state.Value = stateTensor;

                Array.Copy(buffer, WindowSize * sizeof(float), buffer, 0, ContextSize * sizeof(float));
            }
            if(lastSamples>0)
            {
                
                await fs.ReadExactlyAsync(bufferMemory.Slice(ContextSize * sizeof(float), lastSamples * sizeof(float)));
                floatMemory.Span.Slice(ContextSize + lastSamples, WindowSize - lastSamples).Clear();

                var inputs = new List<NamedOnnxValue>
                {
                    input,
                    state,
                    sr,
                };
                using var outputs = _inferenceSession.Run(inputs);
                probabilities[probabilities.Length-1] = outputs[0].AsTensor<float>()[0];
            }

                
            
            return new VADResult(probabilities, WindowSize);
        }
        public async Task<VADResult> DetectVoiceActivity(Stream stream)
        {
            if (!stream.CanRead)
                throw new InvalidOperationException("Stream is not readable.");

            // buffer holds Context + Window floats
            byte[] buffer = new byte[(ContextSize + WindowSize) * sizeof(float)];
            Memory<byte> bufferMem = buffer;
            Memory<float> floatMem = Unsafe.As<Memory<byte>, Memory<float>>(ref bufferMem)[..(ContextSize + WindowSize)];
            var probabilities = new List<float>(); // grows dynamically

            // ONNX fixed inputs
            NamedOnnxValue input =
                NamedOnnxValue.CreateFromTensor("input",
                    new DenseTensor<float>(floatMem, [BatchSize, WindowSize + ContextSize]));

            NamedOnnxValue sr =
                NamedOnnxValue.CreateFromTensor("sr",
                    new DenseTensor<long>(SampleRate, [1], false));

            Tensor<float> stateTensor = new DenseTensor<float>([2, 1, 128]);
            NamedOnnxValue state = NamedOnnxValue.CreateFromTensor("state", stateTensor);

            // ------------------------------
            // MAIN LOOP — read until EOF
            // ------------------------------
            while (true)
            {
                // read exactly a window, or break
                int needed = WindowSize * sizeof(float);
                int offsetBytes = ContextSize * sizeof(float);
                int totalRead = 0;

                // Read until window is full OR EOF
                while (totalRead < needed)
                {
                    int n = await stream.ReadAsync(
                        buffer,
                        offsetBytes + totalRead,
                        needed - totalRead
                    );

                    if (n == 0)
                    {
                        stream.Close();
                        break; // EOF
                    }

                    totalRead += n;
                }

                if (totalRead == 0)
                    break; // no data at all → exit

                if (totalRead < needed)
                {
                    // handle partial window: zero-pad
                    int samplesRead = totalRead / sizeof(float);
                    floatMem.Span
                        .Slice(ContextSize + samplesRead, WindowSize - samplesRead)
                        .Clear();

                    // run inference on partial final window
                    var inputs = new List<NamedOnnxValue> { input, state, sr };
                    using var outputs = _inferenceSession.Run(inputs);
                    probabilities.Add(outputs[0].AsTensor<float>()[0]);

                    break;
                }

                // -------------------
                // Full window → run VAD
                // -------------------
                {
                    var inputs = new List<NamedOnnxValue> { input, state, sr };
                    using var outputs = _inferenceSession.Run(inputs);

                    probabilities.Add(outputs[0].AsTensor<float>()[0]);

                    // update hidden state
                    stateTensor = outputs[1].AsTensor<float>().Clone();
                    state.Value = stateTensor;
                }

                // -------------------
                // Shift last ContextSize floats to front
                // -------------------
                Array.Copy(
                    buffer,
                    WindowSize * sizeof(float),
                    buffer,
                    0,
                    ContextSize * sizeof(float)
                );
            }

            return new VADResult([.. probabilities], WindowSize);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
