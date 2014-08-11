﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using DreamNetwork.PlatformServer.IO;
using DreamNetwork.PlatformServer.Logic;
using MsgPack;
using MsgPack.Serialization;

namespace DreamNetwork.PlatformServer.Networking
{
    public abstract class Message
    {
        // TODO: generate a message id for packets from client to server. ideally sequential or generated by client.

        // TODO: Implement plugin framework-like behavior for message serialization
        private static CompositionContainer _packetContainer;
        internal readonly List<Manager> HandledByManagers = new List<Manager>();

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

        internal uint RequestId { get; set; }

        internal MessageDirection MessageDirections
        {
            get
            {
                return GetType().GetCustomAttributes(typeof (MessageAttribute), false)
                    .Cast<MessageAttribute>()
                    .Single()
                    .Directions;
            }
        }

        internal uint MessageTypeId
        {
            get
            {
                return GetType().GetCustomAttributes(typeof (MessageAttribute), false)
                    .Cast<MessageAttribute>()
                    .Single()
                    .Type;
            }
        }

        public bool HandledBy<T>()
        {
            return HandledByManagers.Any(m => m is T);
        }

        public bool HandledBy(Manager manager)
        {
            return HandledByManagers.Any(m => m == manager);
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

                    var msctx = new SerializationContext();
                    var type = GetType();
                    if (type.GetProperties().Any())
                    {
                        var mser = msctx.GetSerializer(type);
                        mser.Pack(mw.BaseStream, this);
                    }
                    else
                    {
                        var mser = msctx.GetSerializer(typeof (object));
                        mser.Pack(mw.BaseStream, null);
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

                    var msctx = new SerializationContext();
                    Message msg;
                    if (type.GetProperties().Any())
                    {
                        var mser = msctx.GetSerializer(type);
                        msg = mser.Unpack(mr.BaseStream) as Message;
                        msg = NormalizeDecodedObject(msg) as Message;
                    }
                    else
                    {
                        msg = Activator.CreateInstance(type) as Message;
                    }

                    if (msg == null)
                        return null;

                    msg.RequestId = requestId;
                    return msg;
                }
            }
        }

        private static object NormalizeDecodedObject(object obj)
        {
            var objType = obj.GetType();
            foreach (var objProp in objType
                .GetProperties()
                .Where(p => p.CanWrite && p.CanRead))
            {
                var objValue = NormalizeDecodedValue(objProp.GetValue(obj, null));
                objProp.SetValue(obj, objValue, null);
            }
            return obj;
        }

        private static object NormalizeDecodedValue(object objValue)
        {
            if (!(objValue is MessagePackObject))
                return objValue;

            var mpObj = (MessagePackObject) objValue;
            if (mpObj.IsArray)
            {
                // I wonder if char[] needs special treatment
                objValue = NormalizeDecodedArray(mpObj.ToObject() as Array);
            }
            else if (mpObj.IsDictionary /* = mpObj.IsMap */)
            {
                objValue = NormalizeDecodedDictionary(mpObj.AsDictionary());
            }
            else if (mpObj.IsList)
            {
                objValue = NormalizeDecodedList(mpObj.AsList());
            }
            else if (mpObj.IsRaw)
            {
                objValue = mpObj.AsBinary();
            }
            else if (mpObj.IsNil)
            {
                objValue = null;
            }
            return NormalizeDecodedObject(objValue);
        }

        private static List<object> NormalizeDecodedList(IEnumerable<MessagePackObject> list)
        {
            return list.Select(item => NormalizeDecodedValue(item.ToObject())).ToList();
        }

        private static Dictionary<object, object> NormalizeDecodedDictionary(MessagePackObjectDictionary dict)
        {
            return
                dict.Select(
                    item => new {Key = NormalizeDecodedValue(item.Key), Value = NormalizeDecodedValue(item.Value)})
                    .ToDictionary(i => i.Key, i => i.Value);
        }

        private static object[] NormalizeDecodedArray(IEnumerable arr)
        {
            var obj = arr.Cast<object>().ToArray();
            if (obj.All(o => o is MessagePackObject))
                obj = NormalizeDecodedList(obj.Cast<MessagePackObject>()).ToArray();
            return obj;
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