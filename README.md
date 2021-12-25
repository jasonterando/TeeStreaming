
# TeeStreaming

This .NET assembly implements a TeeStream, which is a stream construct which, when written to, can output the data to multiple sink streams.
The TeeStream can also be used as a readable stream itself.

## Usage

To create a TeeStream, it's consructor with the sink streams you want to redirect output to.

```c#
async Task RedirectStream(Stream source)
{
    using (var dest1 = new FileStream("/foo"))
    using (var dest2 = new MemoryStream())
    using (var tee = new TeeStram(dest1, dest2))
    {
        await source.CopyToAsync(tee);
        // ...
    }
}
```

You can also specify a buffer size to facilitate larger read/write blocks than the default of 81,920.

```c#
async Task RedirectStream(Stream source)
{
    using (var dest1 = new FileStream("/foo"))
    using (var dest2 = new MemoryStream())
    using (var tee = new TeeStram(256000, dest1, dest2))
    {
        await source.CopyToAsync(tee);
        // ...
    }
}
```

> **Always be sure to Close or Dispose of this tream when done with it to automatically flush any buffered contents.**


### Working With Non-Seekable Streams - **SetAtEnd()**

The **SetAtEnd()** method indicates that the source stream has been completely
read.  The default **CopyTo**/**CopyToAsync** methods simply read from the 
source and write to the destination stream in blocks.  However, there is no way for the destination stream to "know" that the source has been completely read.

This matters for TeeStream because it needs to return zero from **Read**/**ReadAsync** when sink streams are reading from it, once the source stream
has been consumed.

The **SetAtEnd** method instructs TeeStream to return zero on 
Read/ReadAsync, which lets the sinks know that the source stream is done.

```c#
async Task RedirectStream(Stream source)
{
    using (var dest1 = new FileStream("/foo"))
    using (var dest2 = new MemoryStream())
    using (var tee = new TeeStram(256000, dest1, dest2))
    {
        await source.CopyToAsync(tee);
        tee.SetAtEnd();
    }
}
```

### Optimized Copying - **CopyFromAsync()**

TeeStream's **CopyFromAsync()** method is the reverse of **CopyToAsync**, in that the method will pull all data from the source stream and know when it is completely done.  

```c#
async Task RedirectStream(Stream source)
{
    using (var dest1 = new FileStream("/foo"))
    using (var dest2 = new MemoryStream())
    using (var tee = new TeeStram(256000, dest1, dest2))
    {
        await tee.CopyFromAsync(source);
    }
}
```

This method deploys a couple of tricks to enhance performance.  First, it will buffer incoming data and then write it out in bigger chunks to the sinks.  If you are pulling data from a stream that can only be read in small chunks (like output from a console application), this helps by consolidating write calls to the sinks.  Also, reads and writes (when ready) are triggered simultaneously as opposed to sequentially.

### Using the TeeStream as an Input

You can use the TeeStream as an input by specifing **TeeStream.Self** as a sink.  See the following example.

## Example: Thumbnail Generation

Assume you are working on an application where somebody uploads an image file via HTTP post in ASP.NET, with the Response.Body a stream to that file data.  You want to save that image to storage, generate a thumbnail, save the thumbail to storage, and return the thumbnail as the Response.Body.  

Furthermore, because the image may be a PDF, you want to use ImageMagick's **convert** utility.

You can do this without temp files or large MemoryStreams by doing the following:

```c#
async Task SaveImage(Stream requestBody, Stream responseBody, string imageFileName, string thumbnailFileName)
{
    using (var fsImage = new FileStream(imageFileName, FileMode.Create))
    using (var fsThumbnail = new FileStream(thumbnailFileName, FileMode.Create))
    using (var teeThumbnail = new TeeStream(fsThumbnail, reesponseBody))
    using (var teeInput = new FileStream(fsImage, TeeStream.Self))
    {
        var taskGenerate = Task.Run(async () => {
            var info = new ProcessStartInfo
            {
                FileName = "convert",
                Arguments = $"-resize \"200x200\" - jpg:-",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = Process.Start(info);
            if (process == null) {
                throw new Exception($"Unable to start \"convert");
            }

            await teeInput.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
            await process.StandardOutput.BaseStream.CopyToAsync(teeThumbnail);
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) {
                throw new Exception(stderr.Length > 0 ? stderr : $"Error converting image, exit code: {process.ExitCode}");
            }        
        });
        await teeInput.CopyFromAsync(requestBody);
        taskGenerate.Wait();
    }
}
```

What's happening?

1. We are creating file streams to save the image and thumbnail to.  You can write to any stream.
2. We create **teeInput** stream, which will output to a file for the full sized image and also allow the tee stream itself to be read from.  This will be fed into ImageMagick's convert utility.
3. We create a **teeThumbnail** stream that will direct data to a thumbnail file and the response body.
4. We create a task to call the ImageMagick convert utility, set to read from STDIN and write to STDOUT.  We will redirect both STDIN and STDOUT.  We will copy **teeInput** to STDIN, and copy STDOUT to **teeThumbnail**.  Importantly, we launch this as a Task but do not wait for it, yet.
5. We then copy the request body to **teeInput**.  The task we created above will read this data from **teeInput** and, via **teeThumbnail**, save the thumbnail to a file and the response body stream
6.  Finally, we wait for the task to complete.

## Demonstration

There is a project on [GitHub](https://github.com/jasonterando/TeeStreamingDemo) demonstrating how to tee stream uploads to S3.