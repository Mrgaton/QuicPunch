using QuicNet.Updates;
if (args.Length == 3 && args[0] == "compress") await UpdateCompression.CompressAsync(args[1], args[2]);
else if (args.Length == 4 && args[0] == "decompress") await UpdateCompression.DecompressAsync(args[1], args[2], long.Parse(args[3]));
else throw new ArgumentException("compress input output | decompress input output expectedSize");
