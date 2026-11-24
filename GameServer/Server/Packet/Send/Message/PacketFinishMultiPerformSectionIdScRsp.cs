using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Message;

public class PacketFinishMultiPerformSectionIdScRsp : BasePacket
{
    public PacketFinishMultiPerformSectionIdScRsp(IEnumerable<FPDBFGBJOFD> payloads) : base(
        CmdIds.FinishMultiPerformSectionIdScRsp)
    {
        var rsp = new FinishMultiPerformSectionIdScRsp();
        foreach (var payload in payloads)
        {
            if (payload == null) continue;
            var data = new LGMDACIPFHL
            {
                MGPGDLIJKLM = payload.MGPGDLIJKLM,
                EBMIJAAKMMH = new ItemList()
            };
            foreach (var item in payload.DPPAFBAHEFH)
                data.DPPAFBAHEFH.Add(new MessageItem
                {
                    ItemId = item.ItemId
                });
            rsp.JCALNIODLLN.Add(data);
        }

        SetData(rsp);
    }
}
