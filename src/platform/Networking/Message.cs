﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using DreamNetwork.PlatformServer.IO;
using DreamNetwork.PlatformServer.Logic;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

namespace DreamNetwork.PlatformServer.Networking
{
    public abstract class Message
    {
        // TODO: generate a message id for packets from client to server. ideally sequential or generated by client.

        // TODO: Implement plugin framework-like behavior for message serialization
        private static CompositionContainer _packetContainer;
        internal readonly List<Manager> HandledByManagers = new List<Manager>();

        public bool HandledBy<T>()
        {
            return HandledByManagers.Any(m => m is T);
        }

        public bool HandledBy(Manager manager)
        {
            return HandledByManagers.Any(m => m == manager);
        }

        protected static CompositionContainer PacketContainer
        {
            get
            {
                return _packetContainer ??
                       (_packetContainer =
                           new CompositionContainer(
                               new AggregateCatalog(new AssemblyCatalog(Assembly.GetExecutingAssembly()))));
            }
        }

        [JsonIgnore]
        public uint RequestId { get; internal set; }

        [JsonIgnore]
        private MessageDirection MessageDirections
        {
            get
            {
                return GetType().GetCustomAttributes(typeof (MessageAttribute), false)
                    .Cast<MessageAttribute>()
                    .Single()
                    .Directions;
            }
        }

        [JsonIgnore]
        public uint MessageTypeId
        {
            get
            {
                return GetType().GetCustomAttributes(typeof (MessageAttribute), false)
                    .Cast<MessageAttribute>()
                    .Single()
                    .Type;
            }
        }

        /// <summary>
        ///     Clones the given response with the request ID set to the request message's ID.
        /// </summary>
        /// <typeparam name="T">Response message type</typeparam>
        /// <param name="request">The request message from which to set the request ID</param>
        /// <param name="response">The response message to set the request ID on</param>
        /// <returns>Response message with changed request ID</returns>
        public static T CloneResponse<T>(Message request, T response) where T : Message
        {
            var msg = response.MemberwiseClone() as T;

            if (msg.MessageDirections != MessageDirection.ToClient)
                throw new InvalidOperationException();
            if (request.MessageDirections != MessageDirection.ToServer)
                throw new InvalidOperationException();

            msg.RequestId = request.RequestId;

            return msg;
        }

        /// <summary>
        ///     Serializes this message into a byte array.
        /// </summary>
        /// <returns>
        ///     Serialized message. Structure is 4 bytes request ID, 4 bytes message type ID and the rest is BSON-encoded
        ///     message body.
        /// </returns>
        public byte[] Serialize()
        {
            byte[] message;
            using (var ms = new MemoryStream())
            {
                using (
                    var mw = new EndianBinaryWriter(new BigEndianBitConverter(), new NonClosingStreamWrapper(ms),
                        Encoding.UTF8))
                {
                    // message header
                    mw.Write(RequestId); // req id (4 bytes, uint) [0 if background message]
                    mw.Write(MessageTypeId); // msg type (4 bytes, uint)
                    mw.Flush();

                    using (var mbson = new BsonWriter(new NonClosingStreamWrapper(mw.BaseStream)))
                    {
                        new JsonSerializer().Serialize(mbson, this);
                    }
                }

                message = ms.ToArray();
            }

            return message;
        }

        /// <summary>
        ///     Deserializes a message from a byte array.
        /// </summary>
        /// <param name="direction">The direction of the message.</param>
        /// <param name="data">The data to deserialize.</param>
        /// <returns>Deserialized message.</returns>
        // TODO: Implement conditions for direction and data
        public static Message Deserialize(MessageDirection direction, byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                using (
                    var mr = new EndianBinaryReader(new BigEndianBitConverter(), new NonClosingStreamWrapper(ms),
                        Encoding.UTF8))
                {
                    var requestId = mr.ReadUInt32();
                    var typeId = mr.ReadUInt32();
                    var type = GetMessageTypeById(direction, typeId);
                    if (type == null)
                        throw new ProtocolViolationException(
                            string.Format("No class found to handle packet of type 0x{0:X8}", typeId));

                    using (var mbson = new BsonReader(new NonClosingStreamWrapper(ms)))
                    {
                        var msg = new JsonSerializer().Deserialize(mbson, type) as Message;
                        if (msg == null)
                            throw new AmbiguousMatchException("Deserialized a non-message type from the stream.");
                        msg.RequestId = requestId;
                        return msg;
                    }
                }
            }
        }

        public static Type GetMessageTypeById(MessageDirection direction, uint typeId)
        {
            return Assembly.GetExecutingAssembly().GetTypes().Single(t =>
            {
                if (!t.IsSubclassOf(typeof (Message)))
                    return false;
                var a = t.GetCustomAttributes(typeof (MessageAttribute), false).SingleOrDefault() as MessageAttribute;
                if (a == null)
                    return false;
                return a.Type == typeId && a.Directions == direction;
            });
        }
    }
}